using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
using WalletPK.Messaging;
using static WalletPK.Fraud.IntegrationTests.Infrastructure.FraudSteps;

namespace WalletPK.Fraud.IntegrationTests;

/// <summary>M10.1-M10.4: real-time scores over gRPC from Redis feature windows and the built-in rules.</summary>
[Collection(FraudCollection.Name)]
public class ScoringTests(FraudFactory factory)
{
    [Fact]
    public async Task An_ordinary_payment_is_allowed_in_well_under_20_ms()
    {
        var user = await factory.UserAsync();
        var friend = NewNumber();
        await factory.ScoreAsync(user, 10_000, friend); // warm up the channel

        var timings = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var other = await factory.UserAsync();
            var stopwatch = Stopwatch.StartNew();
            var response = await factory.ScoreAsync(other, 50_000, NewNumber());
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            response.Action.ShouldBe(RiskAction.Allow);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        median.ShouldBeLessThan(20, $"median {median:0.0} ms, p95 {timings[(int)(timings.Count * 0.95)]:0.0} ms");
    }

    [Fact]
    public async Task The_tenth_payment_in_a_minute_is_held_for_review_with_an_alert_and_a_case()
    {
        var user = await factory.UserAsync();
        for (var i = 1; i <= 9; i++)
        {
            (await factory.ScoreAsync(user, 5_000, NewNumber())).Action.ShouldBe(RiskAction.Allow, $"payment {i}");
        }

        var tenth = await factory.ScoreAsync(user, 5_000, NewNumber());

        tenth.Action.ShouldBe(RiskAction.Review);
        tenth.Rules.ShouldContain("velocity-burst");
        tenth.Reason.ShouldContain("Too many payments");

        using var compliance = factory.ClientFor("compliance");
        var alerts = await GetAsync(compliance, $"/api/fraud/alerts?userId={user.Id}");
        var alert = alerts.EnumerateArray().ShouldHaveSingleItem();
        alert.GetProperty("severity").GetString().ShouldBe("Medium");
        alert.GetProperty("action").GetString().ShouldBe("Review");
        var caseId = alert.GetProperty("caseId").GetGuid();
        (await GetAsync(compliance, $"/api/fraud/cases/{caseId}")).GetProperty("case").GetProperty("status").GetString().ShouldBe("Open");

        // Refused once: the next try also carries the "trying again" score.
        var after = await factory.ScoreAsync(user, 5_000, NewNumber());
        after.Rules.ShouldContain("repeat-after-flag");
    }

    [Fact]
    public async Task A_retry_of_the_same_transaction_gets_the_same_answer_and_counts_once()
    {
        var user = await factory.UserAsync();
        var transaction = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            (await factory.ScoreAsync(user, 5_000, "+923009990000", transactionId: transaction)).Action.ShouldBe(RiskAction.Allow);
        }

        // Had the retries counted, this would be the tenth payment in the minute.
        for (var i = 0; i < 7; i++)
        {
            (await factory.ScoreAsync(user, 5_000, NewNumber())).Action.ShouldBe(RiskAction.Allow);
        }
    }

    [Fact]
    public async Task A_sim_swap_drain_is_blocked_and_the_wallets_frozen()
    {
        // An old account on a phone trusted 30 minutes ago: two payments pass, the third is a drain.
        var victim = await factory.UserAsync(accountDays: 400, deviceHours: 0.5);
        (await factory.ScoreAsync(victim, 400_000, NewNumber())).Action.ShouldBe(RiskAction.Allow);
        (await factory.ScoreAsync(victim, 400_000, NewNumber())).Action.ShouldBe(RiskAction.Allow);

        var third = await factory.ScoreAsync(victim, 400_000, NewNumber());

        third.Action.ShouldBe(RiskAction.Block);
        third.Score.ShouldBeGreaterThanOrEqualTo(90);
        third.Rules.ShouldContain("sim-swap-drain");

        var commands = factory.ReadUntil(KafkaTopics.LedgerCommands, read => read.Any(m => m.EventType == "ledger.freeze_wallets" && m.Payload.GetProperty("userId").GetGuid() == victim.Id));
        var freeze = commands.Single(m => m.EventType == "ledger.freeze_wallets" && m.Payload.GetProperty("userId").GetGuid() == victim.Id);
        freeze.Payload.GetProperty("reason").GetString()!.ShouldContain("sim-swap-drain");

        using var compliance = factory.ClientFor("compliance");
        var account = await GetAsync(compliance, $"/api/fraud/accounts/{victim.Id}");
        account.GetProperty("frozen").GetBoolean().ShouldBeTrue();
        account.GetProperty("ledgerConfirmed").GetBoolean().ShouldBeFalse();

        // The Ledger confirms.
        await factory.PublishAsync(new WalletsFreezeChanged(freeze.Payload.GetProperty("requestId").GetGuid(), victim.Id, true, 1, DateTimeOffset.UtcNow));
        await WaitAsync(() => GetAsync(compliance, $"/api/fraud/accounts/{victim.Id}"), a => a.GetProperty("ledgerConfirmed").GetBoolean(), "ledger confirmation");

        var alerts = factory.ReadUntil(KafkaTopics.FraudAlerts, read =>
            read.Any(m => m.EventType == "fraud.alert_raised" && m.Payload.GetProperty("userId").GetGuid() == victim.Id)
            && read.Any(m => m.EventType == "fraud.account_freeze_changed" && m.Payload.GetProperty("userId").GetGuid() == victim.Id));
        alerts.Single(m => m.EventType == "fraud.alert_raised" && m.Payload.GetProperty("userId").GetGuid() == victim.Id).Payload.GetProperty("severity").GetString().ShouldBe("High");
    }

    [Fact]
    public async Task A_large_payment_from_a_new_phone_is_held_but_a_new_users_first_phone_is_not()
    {
        var swapped = await factory.UserAsync(accountDays: 90, deviceHours: 3);
        var newcomer = await factory.UserAsync(accountDays: 0.1, deviceHours: 2);

        (await factory.ScoreAsync(swapped, 1_500_000, NewNumber(), "BankTransfer")).Action.ShouldBe(RiskAction.Review);
        (await factory.ScoreAsync(newcomer, 1_500_000, NewNumber(), "BankTransfer")).Action.ShouldBe(RiskAction.Allow);
    }

    [Fact]
    public async Task Identity_events_tell_the_scorer_when_a_user_registered_and_a_device_was_trusted()
    {
        var user = new TestUser(Guid.NewGuid(), NewNumber(), Guid.NewGuid());
        await factory.PublishAsync(new UserRegistered(user.Id, user.WalletNumber, "L1", DateTimeOffset.UtcNow.AddDays(-200)));
        await factory.PublishAsync(new DeviceBound(user.Id, user.DeviceId, null, DateTimeOffset.UtcNow.AddMinutes(-10)));

        // Wait until the consumer has stored the device, then score once.
        var redis = factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await redis.KeyExistsAsync($"fraud:device:{user.Id:N}:{user.DeviceId}") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Cancel);
        }

        var response = await factory.ScoreAsync(user, 1_200_000, NewNumber(), "CashOut");
        response.Action.ShouldBe(RiskAction.Review);
        response.Rules.ShouldContain("new-device-large");
        (await factory.Features.OwnerOfAsync(user.WalletNumber)).ShouldBe(user.Id);
    }

    [Fact]
    public async Task Bad_requests_are_invalid_arguments()
    {
        var error = await Should.ThrowAsync<Grpc.Core.RpcException>(async () =>
            await factory.Risk.ScoreAsync(new ScoreRequest { TransactionId = "nope", UserId = Guid.NewGuid().ToString(), TransactionType = "P2PTransfer", AmountPaisa = 100 }, cancellationToken: Cancel));

        error.StatusCode.ShouldBe(Grpc.Core.StatusCode.InvalidArgument);
    }
}
