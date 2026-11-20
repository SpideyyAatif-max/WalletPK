using System.Net;
using System.Net.Http.Json;
using WalletPK.Agents.IntegrationTests.Infrastructure;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using static WalletPK.Agents.IntegrationTests.Infrastructure.AgentSteps;

namespace WalletPK.Agents.IntegrationTests;

/// <summary>M9.1: applying, approval, the float, suspension, and finding agents.</summary>
[Collection(AgentsCollection.Name)]
public class OnboardingTests(AgentsFactory factory)
{
    [Fact]
    public async Task An_application_waits_for_approval_then_the_ledger_opens_the_float()
    {
        using var owner = factory.Customer();
        using var applied = await PostAsync(owner.Client, "/api/agents", new { shopName = "Al-Madina Easy Load", city = "Lahore", area = "Ichhra" });
        applied.StatusCode.ShouldBe(HttpStatusCode.Created);
        var agent = await JsonAsync(applied);
        var id = agent.GetProperty("id").GetGuid();
        agent.GetProperty("status").GetString().ShouldBe("PendingApproval");
        agent.GetProperty("code").GetString()!.ShouldMatch("^WPKA[0-9]{6}$");
        factory.Ledger.CommandsFor(id).ShouldBeEmpty();

        using var ops = factory.Operator();
        var pending = await GetAsync(ops, "/api/agents/ops/pending");
        pending.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(id);

        using (var approved = await PostAsync(ops, $"/api/agents/ops/{id}/approve", null))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await JsonAsync(approved)).GetProperty("status").GetString().ShouldBe("Opening");
        }

        var active = await WaitAsync(() => GetAsync(owner.Client, "/api/agents/me"), me => me.GetProperty("status").GetString() == "Active", "active");
        active.GetProperty("lowFloatPaisa").GetInt64().ShouldBe(500_000);
        var open = factory.Ledger.CommandsFor(id).OfType<OpenAgentWallet>().ShouldHaveSingleItem();
        open.OwnerUserId.ShouldBe(owner.Id);
        open.AgentCode.ShouldBe(agent.GetProperty("code").GetString());

        var events = factory.ReadUntil(KafkaTopics.AgentsEvents, read => read.Any(e => e.EventType == "agents.agent_status_changed" && e.Payload.GetProperty("agentId").GetGuid() == id && e.Payload.GetProperty("status").GetString() == "Active"));
        events.ShouldContain(e => e.EventType == "agents.agent_status_changed" && e.Payload.GetProperty("agentId").GetGuid() == id);

        using var again = await PostAsync(owner.Client, "/api/agents", new { shopName = "Second shop", city = "Lahore" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(again)).ShouldBe("agent.already_registered");
    }

    [Fact]
    public async Task Applying_needs_a_verified_cnic_and_a_name_and_city()
    {
        using var unverified = factory.Customer(kycTier: "L0");
        using (var response = await PostAsync(unverified.Client, "/api/agents", new { shopName = "Good Shop", city = "Lahore" }))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ErrorCodeAsync(response)).ShouldBe("agent.kyc_required");
        }

        using var verified = factory.Customer();
        using (var response = await PostAsync(verified.Client, "/api/agents", new { shopName = "AB", city = "Lahore" }))
        {
            (await ErrorCodeAsync(response)).ShouldBe("agent.invalid_shop_name");
        }

        using (var response = await PostAsync(verified.Client, "/api/agents", new { shopName = "Good Shop", city = "" }))
        {
            (await ErrorCodeAsync(response)).ShouldBe("agent.invalid_city");
        }
    }

    [Fact]
    public async Task A_rejected_agent_cannot_do_cash_in()
    {
        using var owner = factory.Customer();
        using var applied = await PostAsync(owner.Client, "/api/agents", new { shopName = "Doubtful Shop", city = "Karachi" });
        var id = (await JsonAsync(applied)).GetProperty("id").GetGuid();
        using var ops = factory.Operator();

        using (var rejected = await PostAsync(ops, $"/api/agents/ops/{id}/reject", new { reason = "Shop not found at the address" }))
        {
            var agent = await JsonAsync(rejected);
            agent.GetProperty("status").GetString().ShouldBe("Rejected");
            agent.GetProperty("statusReason").GetString().ShouldBe("Shop not found at the address");
        }

        using var customer = factory.Customer();
        using var cashIn = await PostAsync(owner.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 10_000 }, Guid.NewGuid().ToString());
        cashIn.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(cashIn)).ShouldBe("agent.not_active");
    }

    [Fact]
    public async Task A_suspended_agent_can_only_take_its_float_back_until_it_is_reactivated()
    {
        using var agent = await factory.ActiveAgentAsync(floatPaisa: 200_000);
        using var customer = factory.Customer();
        using var ops = factory.Operator();

        using (var suspended = await PostAsync(ops, $"/api/agents/ops/{agent.Id}/suspend", new { reason = "Complaints" }))
        {
            (await JsonAsync(suspended)).GetProperty("status").GetString().ShouldBe("Suspended");
        }

        using (var cashIn = await PostAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 10_000 }, Guid.NewGuid().ToString()))
        {
            (await ErrorCodeAsync(cashIn)).ShouldBe("agent.not_active");
        }

        using (var load = await PostAsync(agent.Client, "/api/agents/me/float", new { direction = "ToFloat", amountPaisa = 10_000 }, Guid.NewGuid().ToString()))
        {
            (await ErrorCodeAsync(load)).ShouldBe("agent.not_active");
        }

        var back = await StartAsync(agent.Client, "/api/agents/me/float", new { direction = "ToWallet", amountPaisa = 50_000 });
        (await WaitForTransactionAsync(agent.Client, back)).GetProperty("status").GetString().ShouldBe("Completed");
        factory.Ledger.Float(agent.Owner.Id).ShouldBe(150_000);

        using (var reactivated = await PostAsync(ops, $"/api/agents/ops/{agent.Id}/reactivate", null))
        {
            (await JsonAsync(reactivated)).GetProperty("status").GetString().ShouldBe("Active");
        }

        var cashInId = await StartAsync(agent.Client, "/api/agents/me/cash-ins", new { customerNumber = customer.Phone, amountPaisa = 10_000 });
        (await WaitForTransactionAsync(agent.Client, cashInId)).GetProperty("status").GetString().ShouldBe("Completed");
    }

    [Fact]
    public async Task Customers_cannot_use_the_operations_endpoints()
    {
        using var customer = factory.Customer();

        using var pending = await customer.Client.GetAsync(new Uri("/api/agents/ops/pending", UriKind.Relative), Cancel);
        using var run = await PostAsync(customer.Client, "/api/agents/ops/payouts/run", null);

        pending.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        run.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Customers_find_active_agents_by_city_and_code()
    {
        var city = $"Town{Random.Shared.Next(1000, 9999)}";
        using var agent = await factory.ActiveAgentAsync(shop: "Zam Zam Easy Cash", city: city);
        using var customer = factory.Customer();

        var found = await GetAsync(customer.Client, $"/api/agents/find?city={city.ToLowerInvariant()}");
        found.GetArrayLength().ShouldBe(1);
        found[0].GetProperty("code").GetString().ShouldBe(agent.Code);
        found[0].GetProperty("shopName").GetString().ShouldBe("Zam Zam Easy Cash");

        (await GetAsync(customer.Client, $"/api/agents/by-code/{agent.Code.ToLowerInvariant()}")).GetProperty("city").GetString().ShouldBe(city);
        using var unknown = await customer.Client.GetAsync(new Uri("/api/agents/by-code/WPKA000000", UriKind.Relative), Cancel);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Float_moves_both_ways_and_a_move_beyond_the_wallet_fails()
    {
        using var agent = await factory.ActiveAgentAsync(floatPaisa: 100_000);
        factory.Ledger.Float(agent.Owner.Id).ShouldBe(100_000);
        factory.Ledger.Wallet(agent.Owner.Id).ShouldBe(0);

        var back = await StartAsync(agent.Client, "/api/agents/me/float", new { direction = "ToWallet", amountPaisa = 40_000 });
        var moved = await WaitForTransactionAsync(agent.Client, back);
        moved.GetProperty("kind").GetString().ShouldBe("FloatOut");
        moved.GetProperty("agentBalancePaisa").GetInt64().ShouldBe(60_000);

        var tooMuch = await StartAsync(agent.Client, "/api/agents/me/float", new { direction = "ToFloat", amountPaisa = 50_000 });
        var failed = await WaitForTransactionAsync(agent.Client, tooMuch);
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");

        using var wrong = await PostAsync(agent.Client, "/api/agents/me/float", new { direction = "Sideways", amountPaisa = 10_000 }, Guid.NewGuid().ToString());
        (await ErrorCodeAsync(wrong)).ShouldBe("agent.invalid_direction");
    }

    [Fact]
    public async Task The_float_alert_level_can_be_changed()
    {
        using var agent = await factory.ActiveAgentAsync();

        using var response = await agent.Client.PutAsJsonAsync(new Uri("/api/agents/me/low-float", UriKind.Relative), new { lowFloatPaisa = 1_000_000 }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("lowFloatPaisa").GetInt64().ShouldBe(1_000_000);
    }
}
