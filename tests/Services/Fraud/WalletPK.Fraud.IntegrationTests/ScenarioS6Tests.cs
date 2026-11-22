using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
using WalletPK.Fraud.Infrastructure.Persistence;
using WalletPK.Messaging;
using static WalletPK.Fraud.IntegrationTests.Infrastructure.FraudSteps;

namespace WalletPK.Fraud.IntegrationTests;

/// <summary>
/// Scenario S6 from the proposal (NFR-5.1): 20 compromised accounts log in from new devices and try to drain
/// funds to 3 mule wallets. Pass: the rules engine flags the pattern, freezes the accounts and raises alerts
/// within 2 seconds.
/// </summary>
[Collection(FraudCollection.Name)]
public class ScenarioS6Tests(FraudFactory factory)
{
    [Fact]
    public async Task A_SIM_swap_ring_of_20_accounts_and_3_mules_is_frozen_and_alerted_within_2_seconds()
    {
        var mules = new List<TestUser>();
        for (var i = 0; i < 3; i++)
        {
            mules.Add(await factory.UserAsync(accountDays: 3));
        }

        // Long-standing customers whose SIMs were swapped: the attacker logged in on a new phone an hour ago.
        var victims = new List<TestUser>();
        for (var i = 0; i < 20; i++)
        {
            victims.Add(await factory.UserAsync(accountDays: 400, deviceHours: 1));
        }

        using var compliance = factory.ClientFor("compliance");

        // The 2 seconds are for a running service: one blocked payment first, so the clock does not also time
        // .NET compiling the scoring, alert and freeze code for the first time (S6 may be the first test to run).
        var warmUp = await factory.UserAsync(accountDays: 400, deviceHours: 1);
        (await factory.ScoreAsync(warmUp, 4_000_000, (await factory.UserAsync()).WalletNumber)).Action.ShouldBe(RiskAction.Block);

        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        // Every account tries to empty Rs. 40,000 into one of the mules, all at once.
        var answers = await Task.WhenAll(victims.Select((victim, i) => factory.ScoreAsync(victim, 4_000_000, mules[i % 3].WalletNumber)));
        var decided = watch.Elapsed;

        answers.ShouldAllBe(answer => answer.Action == RiskAction.Block);
        answers.ShouldAllBe(answer => answer.Rules.Contains("sim-swap-drain"));

        // Wait for the outcome in the database (one query per look, not one request per account), then time it
        // with the service's own timestamps: when the last freeze and the last alert were written.
        var everyone = victims.Concat(mules).Select(user => user.Id).ToList();
        var muleIds = mules.Select(mule => mule.Id).ToList();
        DateTimeOffset lastFreeze = default, lastAlert = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
            var frozen = await db.Set<AccountFlag>().AsNoTracking().Where(flag => everyone.Contains(flag.UserId) && flag.Frozen).Select(flag => flag.ChangedAt).ToListAsync(Cancel);
            var alerts = await db.Set<FraudAlert>().AsNoTracking()
                .Where(alert => (alert.Kind == "Ring" && muleIds.Contains(alert.UserId)) || (alert.Kind == "Payment" && alert.Action == "Block" && everyone.Contains(alert.UserId)))
                .Select(alert => new { alert.Kind, alert.UserId, alert.CreatedAt }).ToListAsync(Cancel);
            if (frozen.Count == everyone.Count && alerts.Count(alert => alert.Kind == "Ring") == 3 && alerts.Count(alert => alert.Kind == "Payment") == 20)
            {
                lastFreeze = frozen.Max();
                lastAlert = alerts.Max(alert => alert.CreatedAt);
                break;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"after 15 s: {frozen.Count} of {everyone.Count} frozen, {alerts.Count(a => a.Kind == "Ring")} of 3 rings, {alerts.Count(a => a.Kind == "Payment")} of 20 payment alerts");
            await Task.Delay(25, Cancel);
        }

        var done = (lastFreeze > lastAlert ? lastFreeze : lastAlert) - started;
        TestContext.Current.TestOutputHelper?.WriteLine($"S6: 20 decisions in {decided.TotalMilliseconds:0} ms; the last freeze or alert {done.TotalMilliseconds:0} ms after the attack began");
        done.ShouldBeLessThan(TimeSpan.FromSeconds(2), $"decisions after {decided.TotalMilliseconds:0} ms, last freeze or alert after {done.TotalMilliseconds:0} ms");

        var cases = await GetAsync(compliance, "/api/fraud/cases?status=Open&limit=200");
        cases.EnumerateArray().Count(c => c.GetProperty("kind").GetString() == "Ring" && muleIds.Contains(c.GetProperty("userId").GetGuid())).ShouldBe(3);

        // The alerts are on fraud.alerts for the compliance dashboard: 20 blocked payments and 3 rings, all High.
        var published = factory.ReadUntil(KafkaTopics.FraudAlerts, read =>
            read.Count(m => m.EventType == "fraud.alert_raised" && m.Payload.GetProperty("severity").GetString() == "High"
                && everyone.Contains(m.Payload.GetProperty("userId").GetGuid())) >= 23);
        published.Where(m => m.EventType == "fraud.alert_raised" && everyone.Contains(m.Payload.GetProperty("userId").GetGuid()))
            .Select(m => m.Payload.GetProperty("kind").GetString()).Distinct().ShouldBe(["Payment", "Ring"], ignoreOrder: true);

        // And the Ledger is told to freeze every one of them.
        factory.ReadUntil(KafkaTopics.LedgerCommands, read =>
            everyone.All(user => read.Any(m => m.EventType == "ledger.freeze_wallets" && m.Key == user.ToString())));
    }
}
