using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Contracts.Ledger;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
using WalletPK.Fraud.Infrastructure.Persistence;
using WalletPK.Messaging;
using static WalletPK.Fraud.IntegrationTests.Infrastructure.FraudSteps;

namespace WalletPK.Fraud.IntegrationTests;

/// <summary>M10.2, M10.5-M10.7: the rule editor with hot reload, rings, freezes, cases and the audit chain.</summary>
[Collection(FraudCollection.Name)]
public class ComplianceTests(FraudFactory factory)
{
    [Fact]
    public async Task Only_compliance_officers_and_admins_reach_the_compliance_api()
    {
        foreach (var (role, expected) in new[] { ("customer", HttpStatusCode.Forbidden), ("finance", HttpStatusCode.Forbidden), ("compliance", HttpStatusCode.OK), ("admin", HttpStatusCode.OK) })
        {
            using var client = factory.ClientFor(role);
            using var response = await client.GetAsync(new Uri("/api/fraud/rules", UriKind.Relative), Cancel);
            response.StatusCode.ShouldBe(expected, role);
        }
    }

    [Fact]
    public async Task The_built_in_rules_are_seeded_and_the_feature_catalog_listed()
    {
        using var compliance = factory.ClientFor("compliance");

        var rules = await GetAsync(compliance, "/api/fraud/rules");
        rules.EnumerateArray().Where(rule => rule.GetProperty("builtIn").GetBoolean()).Select(rule => rule.GetProperty("id").GetString())
            .ShouldBe(["mule-fan-in", "new-device-large", "new-recipient-large", "odd-hours-large", "repeat-after-flag", "sim-swap-drain", "velocity-burst"], ignoreOrder: true);

        var features = await GetAsync(compliance, "/api/fraud/features");
        features.EnumerateArray().Select(feature => feature.GetProperty("name").GetString()).ShouldContain("count_60s");
    }

    [Fact]
    public async Task A_new_rule_takes_effect_at_once_and_switching_it_off_takes_it_away()
    {
        using var compliance = factory.ClientFor("compliance");
        var id = $"cash-out-cap-{Random.Shared.Next(1000, 9999)}";
        var user = await factory.UserAsync();
        (await factory.ScoreAsync(user, 4_500_000, type: "CashOut")).Action.ShouldBe(RiskAction.Allow);

        using (var invalid = await PostAsync(compliance, "/api/fraud/rules", new { id, name = "Cash-out cap", definition = JsonDocument.Parse("""{ "when": { "feature": "amount", "op": "gte", "value": 1 } }""").RootElement }))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await JsonAsync(invalid);
            problem.GetProperty("code").GetString().ShouldBe("fraud.invalid_rule");
            problem.GetProperty("detail").GetString()!.ShouldContain("unknown feature \"amount\"");
        }

        var definition = JsonDocument.Parse("""
            { "types": ["CashOut"], "when": { "feature": "amount_paisa", "op": "gte", "value": 4000000 }, "score": 10, "action": "Review", "reason": "Large cash-outs need a check." }
            """).RootElement;
        using (var created = await PostAsync(compliance, "/api/fraud/rules", new { id, name = "Cash-out cap", description = "Rs. 40,000 or more in cash", definition }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Cancel));
        }

        var held = await factory.ScoreAsync(user, 4_500_000, type: "CashOut");
        held.Action.ShouldBe(RiskAction.Review);
        held.Reason.ShouldBe("Large cash-outs need a check.");

        using (var off = await compliance.PutAsJsonAsync(new Uri($"/api/fraud/rules/{id}", UriKind.Relative), new { enabled = false }, Cancel))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await JsonAsync(off)).GetProperty("version").GetInt32().ShouldBe(2);
        }

        var other = await factory.UserAsync();
        (await factory.ScoreAsync(other, 4_500_000, type: "CashOut")).Action.ShouldBe(RiskAction.Allow);

        var audit = await GetAsync(compliance, "/api/fraud/audit");
        audit.EnumerateArray().Where(entry => entry.GetProperty("subject").GetString() == $"rule:{id}").Select(entry => entry.GetProperty("action").GetString())
            .ShouldBe(["rule.create", "rule.update"]);
    }

    [Fact]
    public async Task A_rule_can_be_tried_on_feature_values_without_saving_it()
    {
        using var compliance = factory.ClientFor("compliance");

        using var tried = await PostAsync(compliance, "/api/fraud/rules/test", new
        {
            rule = JsonDocument.Parse("""{ "when": { "feature": "count_1h", "op": "gte", "value": 3 }, "score": 70 }""").RootElement,
            features = new Dictionary<string, object> { ["count_1h"] = 4, ["transaction_type"] = "P2PTransfer" },
        });
        tried.StatusCode.ShouldBe(HttpStatusCode.OK, await tried.Content.ReadAsStringAsync(Cancel));
        var result = await JsonAsync(tried);
        result.GetProperty("score").GetInt32().ShouldBe(70);
        result.GetProperty("action").GetString().ShouldBe("Review");

        using var inForce = await PostAsync(compliance, "/api/fraud/rules/test", new { features = new Dictionary<string, object> { ["count_60s"] = 12, ["transaction_type"] = "P2PTransfer" } });
        (await JsonAsync(inForce)).GetProperty("rules").EnumerateArray().Select(rule => rule.GetString()).ShouldContain("velocity-burst");
    }

    [Fact]
    public async Task A_mule_ring_is_found_and_the_mule_and_the_refused_senders_are_frozen_within_seconds()
    {
        var mule = await factory.UserAsync(accountDays: 2);
        var victims = new List<TestUser>();
        for (var i = 0; i < 3; i++)
        {
            victims.Add(await factory.UserAsync(accountDays: 300, deviceHours: 1));
        }

        var bystanders = new List<TestUser> { await factory.UserAsync(), await factory.UserAsync() };
        var started = DateTime.UtcNow;

        // Three compromised accounts try Rs. 15,000 each from new phones (held), two others pay small amounts.
        foreach (var victim in victims)
        {
            (await factory.ScoreAsync(victim, 1_500_000, mule.WalletNumber)).Action.ShouldNotBe(RiskAction.Allow);
        }

        foreach (var bystander in bystanders)
        {
            await factory.ScoreAsync(bystander, 20_000, mule.WalletNumber);
        }

        using var compliance = factory.ClientFor("compliance");
        var account = await WaitAsync(() => GetAsync(compliance, $"/api/fraud/accounts/{mule.Id}"), a => a.GetProperty("frozen").GetBoolean(), "mule frozen", seconds: 10);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        account.GetProperty("reason").GetString()!.ShouldContain("Mule ring");

        var cases = await GetAsync(compliance, "/api/fraud/cases?status=Open");
        var ring = cases.EnumerateArray().Single(fraudCase => fraudCase.GetProperty("kind").GetString() == "Ring" && fraudCase.GetProperty("userId").GetGuid() == mule.Id);
        ring.GetProperty("subjects").EnumerateArray().Select(subject => subject.GetGuid()).ShouldBe([mule.Id, .. victims.Select(v => v.Id), .. bystanders.Select(b => b.Id)], ignoreOrder: true);

        foreach (var victim in victims)
        {
            (await GetAsync(compliance, $"/api/fraud/accounts/{victim.Id}")).GetProperty("frozen").GetBoolean().ShouldBeTrue();
        }

        foreach (var bystander in bystanders)
        {
            (await GetAsync(compliance, $"/api/fraud/accounts/{bystander.Id}")).GetProperty("frozen").GetBoolean().ShouldBeFalse();
        }

        var commands = factory.ReadUntil(KafkaTopics.LedgerCommands, read => read.Count(m => m.EventType == "ledger.freeze_wallets"
            && (m.Payload.GetProperty("userId").GetGuid() == mule.Id || victims.Any(v => v.Id == m.Payload.GetProperty("userId").GetGuid()))) >= 4);
        commands.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Payments_that_went_through_count_toward_a_wallets_fan_in_and_the_next_sender_must_confirm()
    {
        var recipient = await factory.UserAsync();
        for (var i = 0; i < 4; i++)
        {
            var sender = await factory.UserAsync();
            await factory.PublishAsync(new WalletEntryPosted(
                Guid.NewGuid(), recipient.Id, recipient.WalletNumber, "CustomerWallet", i + 1, Guid.NewGuid(), "P2PTransfer", "Transfer", "Credit", 10_000, 10_000 * (i + 1), sender.WalletNumber, DateTimeOffset.UtcNow));
        }

        var redis = factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await redis.SortedSetLengthAsync($"fraud:fanin:{recipient.WalletNumber}") < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Cancel);
        }

        var fifth = await factory.ScoreAsync(await factory.UserAsync(), 10_000, recipient.WalletNumber);
        fifth.Rules.ShouldContain("mule-fan-in");
        fifth.Score.ShouldBe(40);
        fifth.Action.ShouldBe(RiskAction.StepUp);
        fifth.Reason.ShouldContain("Make sure you know who you are paying");
    }

    [Fact]
    public async Task A_compliance_officer_freezes_and_unfreezes_with_a_reason_and_it_is_audited()
    {
        using var compliance = factory.ClientFor("compliance");
        var user = Guid.NewGuid();

        using (var noReason = await PostAsync(compliance, $"/api/fraud/accounts/{user}/freeze", new { reason = "" }))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var frozen = await PostAsync(compliance, $"/api/fraud/accounts/{user}/freeze", new { reason = "Customer reported a stolen phone" }))
        {
            frozen.StatusCode.ShouldBe(HttpStatusCode.OK, await frozen.Content.ReadAsStringAsync(Cancel));
        }

        using (var again = await PostAsync(compliance, $"/api/fraud/accounts/{user}/freeze", new { reason = "Twice" }))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var unfrozen = await PostAsync(compliance, $"/api/fraud/accounts/{user}/unfreeze", new { reason = "Phone recovered, identity checked" }))
        {
            unfrozen.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var commands = factory.ReadUntil(KafkaTopics.LedgerCommands, read => read.Count(m => m.Key == user.ToString()) >= 2);
        commands.Where(m => m.Key == user.ToString()).Select(m => m.EventType).ShouldBe(["ledger.freeze_wallets", "ledger.unfreeze_wallets"]);

        var audit = await GetAsync(compliance, "/api/fraud/audit?limit=1000");
        audit.EnumerateArray().Where(entry => entry.GetProperty("subject").GetString() == $"user:{user}").Select(entry => entry.GetProperty("action").GetString())
            .ShouldBe(["account.freeze", "account.unfreeze"]);
        (await GetAsync(compliance, "/api/fraud/audit/verify")).GetProperty("intact").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_case_is_assigned_noted_and_closed_as_a_false_positive_which_unfreezes_its_user()
    {
        using var compliance = factory.ClientFor("compliance");
        var victim = await factory.UserAsync(accountDays: 400, deviceHours: 0.5);
        await factory.ScoreAsync(victim, 400_000, NewNumber());
        await factory.ScoreAsync(victim, 400_000, NewNumber());
        (await factory.ScoreAsync(victim, 400_000, NewNumber())).Action.ShouldBe(RiskAction.Block);

        var alerts = await GetAsync(compliance, $"/api/fraud/alerts?userId={victim.Id}");
        var caseId = alerts.EnumerateArray().First(alert => alert.GetProperty("action").GetString() == "Block").GetProperty("caseId").GetGuid();

        using (var assigned = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/assign", null))
        {
            (await JsonAsync(assigned)).GetProperty("case").GetProperty("status").GetString().ShouldBe("InReview");
        }

        using (var noted = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/notes", new { text = "Called the customer: they bought a new phone and were paying their suppliers." }))
        {
            (await JsonAsync(noted)).GetProperty("notes").GetArrayLength().ShouldBe(1);
        }

        using (var wrong = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/close", new { resolution = "Maybe" }))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var closed = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/close", new { resolution = "FalsePositive", unfreeze = true, note = "Genuine." }))
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync(Cancel));
            var view = await JsonAsync(closed);
            view.GetProperty("case").GetProperty("status").GetString().ShouldBe("Closed");
            view.GetProperty("case").GetProperty("resolution").GetString().ShouldBe("FalsePositive");
            view.GetProperty("notes").GetArrayLength().ShouldBe(2);
        }

        (await GetAsync(compliance, $"/api/fraud/accounts/{victim.Id}")).GetProperty("frozen").GetBoolean().ShouldBeFalse();
        factory.ReadUntil(KafkaTopics.LedgerCommands, read => read.Any(m => m.EventType == "ledger.unfreeze_wallets" && m.Key == victim.Id.ToString()));

        using var closedAgain = await PostAsync(compliance, $"/api/fraud/cases/{caseId}/notes", new { text = "late" });
        closedAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_case_can_be_opened_by_hand()
    {
        using var compliance = factory.ClientFor("compliance");
        var user = Guid.NewGuid();

        using var opened = await PostAsync(compliance, "/api/fraud/cases", new { userId = user, title = "Tip-off from a bank about this customer" });

        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        var view = await JsonAsync(opened);
        view.GetProperty("case").GetProperty("kind").GetString().ShouldBe("Manual");
        view.GetProperty("case").GetProperty("number").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Tampering_with_the_audit_log_breaks_the_chain()
    {
        using var compliance = factory.ClientFor("compliance");
        using (await PostAsync(compliance, $"/api/fraud/accounts/{Guid.NewGuid()}/freeze", new { reason = "Audit tamper test" }))
        {
        }

        var entries = await GetAsync(compliance, "/api/fraud/audit?limit=1000");
        var target = entries.EnumerateArray().First().GetProperty("sequence").GetInt64();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE audit_log SET details = details || ' ' WHERE sequence = {target}", Cancel);

        try
        {
            var verify = await GetAsync(compliance, "/api/fraud/audit/verify");
            verify.GetProperty("intact").GetBoolean().ShouldBeFalse();
            verify.GetProperty("brokenAt").GetInt64().ShouldBe(target);
        }
        finally
        {
            // Put it back, so the other tests sharing this database see an intact chain.
            await db.Database.ExecuteSqlAsync($"UPDATE audit_log SET details = left(details, length(details) - 1) WHERE sequence = {target}", Cancel);
        }

        (await GetAsync(compliance, "/api/fraud/audit/verify")).GetProperty("intact").GetBoolean().ShouldBeTrue();
    }
}
