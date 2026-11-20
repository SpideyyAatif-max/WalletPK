using System.Diagnostics;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
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
        var watch = Stopwatch.StartNew();

        // Every account tries to empty Rs. 40,000 into one of the mules, all at once.
        var answers = await Task.WhenAll(victims.Select((victim, i) => factory.ScoreAsync(victim, 4_000_000, mules[i % 3].WalletNumber)));
        var decided = watch.Elapsed;

        answers.ShouldAllBe(answer => answer.Action == RiskAction.Block);
        answers.ShouldAllBe(answer => answer.Rules.Contains("sim-swap-drain"));

        var everyone = victims.Concat(mules).Select(user => user.Id).ToList();
        await WaitAsync(
            async () =>
            {
                var frozen = 0;
                foreach (var user in everyone)
                {
                    frozen += (await GetAsync(compliance, $"/api/fraud/accounts/{user}")).GetProperty("frozen").GetBoolean() ? 1 : 0;
                }

                return System.Text.Json.JsonSerializer.SerializeToElement(frozen);
            },
            frozen => frozen.GetInt32() == everyone.Count,
            "all 20 accounts and 3 mules frozen",
            seconds: 10);
        var rings = await WaitAsync(
            () => GetAsync(compliance, "/api/fraud/cases?status=Open&limit=200"),
            cases => mules.All(mule => cases.EnumerateArray().Any(c => c.GetProperty("kind").GetString() == "Ring" && c.GetProperty("userId").GetGuid() == mule.Id)),
            "a ring case per mule",
            seconds: 10);
        var done = watch.Elapsed;

        TestContext.Current.TestOutputHelper?.WriteLine($"S6: 20 decisions in {decided.TotalMilliseconds:0} ms; 23 accounts frozen and 3 rings found in {done.TotalMilliseconds:0} ms");
        done.ShouldBeLessThan(TimeSpan.FromSeconds(2), $"decisions after {decided.TotalMilliseconds:0} ms, everything frozen and rings found after {done.TotalMilliseconds:0} ms");
        rings.EnumerateArray().Count(c => c.GetProperty("kind").GetString() == "Ring" && mules.Any(m => m.Id == c.GetProperty("userId").GetGuid())).ShouldBe(3);

        // The alerts are on fraud.alerts for the compliance dashboard: 20 blocked payments and 3 rings, all High.
        var alerts = factory.ReadUntil(KafkaTopics.FraudAlerts, read =>
            read.Count(m => m.EventType == "fraud.alert_raised" && m.Payload.GetProperty("severity").GetString() == "High"
                && everyone.Contains(m.Payload.GetProperty("userId").GetGuid())) >= 23);
        alerts.Where(m => m.EventType == "fraud.alert_raised" && everyone.Contains(m.Payload.GetProperty("userId").GetGuid()))
            .Select(m => m.Payload.GetProperty("kind").GetString()).Distinct().ShouldBe(["Payment", "Ring"], ignoreOrder: true);

        // And the Ledger is told to freeze every one of them.
        factory.ReadUntil(KafkaTopics.LedgerCommands, read =>
            everyone.All(user => read.Any(m => m.EventType == "ledger.freeze_wallets" && m.Key == user.ToString())));
    }
}
