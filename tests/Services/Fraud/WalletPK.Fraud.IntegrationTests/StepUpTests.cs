using Microsoft.Extensions.DependencyInjection;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Contracts.Identity;
using WalletPK.Fraud.IntegrationTests.Infrastructure;
using static WalletPK.Fraud.IntegrationTests.Infrastructure.FraudSteps;

namespace WalletPK.Fraud.IntegrationTests;

/// <summary>FR-5.1: the step-up OTP action, and how a code confirmed in the Identity service lifts it once.</summary>
[Collection(FraudCollection.Name)]
public class StepUpTests(FraudFactory factory)
{
    [Fact]
    public async Task A_large_payment_to_someone_new_needs_a_code_and_a_confirmed_code_lets_one_through_from_that_device()
    {
        var user = await factory.UserAsync();

        var first = await factory.ScoreAsync(user, 3_000_000, NewNumber());
        first.Action.ShouldBe(RiskAction.StepUp);
        first.Rules.ShouldBe(["new-recipient-large"]);
        first.Reason.ShouldBe("This is a large payment to someone you have not paid before.");

        // Not a refusal for risk: trying again does not make it worse.
        var again = await factory.ScoreAsync(user, 3_000_000, NewNumber());
        again.Action.ShouldBe(RiskAction.StepUp);
        again.Score.ShouldBe(30);

        // A code confirmed on another phone does not count for this one.
        await ConfirmAsync(user.Id, Guid.NewGuid());
        (await factory.ScoreAsync(user, 3_000_000, NewNumber())).Action.ShouldBe(RiskAction.StepUp);

        await ConfirmAsync(user.Id, user.DeviceId);
        var transactionId = Guid.NewGuid();
        var confirmed = await factory.ScoreAsync(user, 3_000_000, NewNumber(), transactionId: transactionId);
        confirmed.Action.ShouldBe(RiskAction.Allow);
        confirmed.Rules.ShouldBe(["new-recipient-large"]);
        confirmed.Reason.ShouldBe("Confirmed with a one-time code.");

        // A retry of the same payment keeps its answer; the next large one needs a new code.
        (await factory.ScoreAsync(user, 3_000_000, NewNumber(), transactionId: transactionId)).Action.ShouldBe(RiskAction.Allow);
        await Task.Delay(100, Cancel);
        (await factory.ScoreAsync(user, 3_000_000, NewNumber())).Action.ShouldBe(RiskAction.StepUp);
    }

    [Fact]
    public async Task A_code_does_not_lift_a_hold_or_a_block()
    {
        var user = await factory.UserAsync(accountDays: 300, deviceHours: 2);
        await ConfirmAsync(user.Id, user.DeviceId);

        var result = await factory.ScoreAsync(user, 1_500_000, NewNumber(), "CashOut");

        result.Action.ShouldBe(RiskAction.Review);
        result.Rules.ShouldBe(["new-device-large"]);
    }

    private async Task ConfirmAsync(Guid userId, Guid deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        await factory.PublishAsync(new PaymentStepUpConfirmed(userId, deviceId, now, now.AddMinutes(5)));
        var redis = factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
        var key = $"fraud:stepup:{userId:N}:{deviceId}";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await redis.KeyExistsAsync(key) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Cancel);
        }

        (await redis.KeyExistsAsync(key)).ShouldBeTrue("the step-up confirmation reached Redis");
    }
}
