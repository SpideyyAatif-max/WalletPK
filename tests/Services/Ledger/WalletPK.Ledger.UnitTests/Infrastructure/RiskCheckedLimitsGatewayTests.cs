using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Risk;

namespace WalletPK.Ledger.UnitTests.Infrastructure;

/// <summary>M10.4: the risk score after the limits, with the reservation given back when the score says no.</summary>
public sealed class RiskCheckedLimitsGatewayTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeLimits _limits = new();
    private readonly FakeRisk _risk = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private RiskCheckedLimitsGateway Gateway(bool failOpen = true) => new(
        _limits,
        new Risk.RiskClient(_risk),
        new FraudGrpcOptions { Address = new Uri("http://fraud.test"), FailOpen = failOpen },
        _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
        NullLogger<RiskCheckedLimitsGateway>.Instance);

    private static Task<LimitDecisionResult> ReserveAsync(RiskCheckedLimitsGateway gateway) =>
        gateway.ReserveAsync(new TransactionId(Guid.NewGuid()), new UserId(Guid.NewGuid()), JournalType.P2PTransfer, Money.FromPaisa(500_000), "device-1", "+923001234567", Cancel);

    [Fact]
    public async Task An_allowed_score_keeps_the_reservation_and_sends_the_whole_payment()
    {
        _risk.Answer = new ScoreResponse { Action = RiskAction.Allow, Score = 10 };

        var decision = await ReserveAsync(Gateway());

        decision.Allowed.ShouldBeTrue();
        _limits.Released.ShouldBe(0);
        var request = _risk.Requests.ShouldHaveSingleItem();
        request.TransactionType.ShouldBe("P2PTransfer");
        request.AmountPaisa.ShouldBe(500_000);
        request.Counterparty.ShouldBe("+923001234567");
        request.DeviceId.ShouldBe("device-1");
    }

    [Theory]
    [InlineData(RiskAction.StepUp, RiskDecisions.StepUp, "fraud.step_up_required")]
    [InlineData(RiskAction.Review, RiskDecisions.Review, "fraud.review")]
    [InlineData(RiskAction.Block, RiskDecisions.Blocked, "fraud.blocked")]
    public async Task Step_up_review_and_block_refuse_the_debit_and_give_the_limits_back(RiskAction action, string decisionName, string errorCode)
    {
        _risk.Answer = new ScoreResponse { Action = action, Score = 95, Reason = "Checked by our team." };

        var decision = await ReserveAsync(Gateway());

        decision.Allowed.ShouldBeFalse();
        decision.Decision.ShouldBe(decisionName);
        decision.Reason.ShouldStartWith("Checked by our team.");
        _limits.Released.ShouldBe(1);
        WalletErrors.LimitExceeded(decision).Code.ShouldBe(errorCode);
    }

    [Fact]
    public async Task Over_the_limits_the_risk_score_is_not_asked()
    {
        _limits.Allow = false;

        var decision = await ReserveAsync(Gateway());

        decision.Decision.ShouldBe("ExceedsDaily");
        _risk.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task When_the_fraud_service_does_not_answer_the_payment_follows_the_fail_open_setting(bool failOpen, bool allowed)
    {
        _risk.Fail = true;

        var decision = await ReserveAsync(Gateway(failOpen));

        decision.Allowed.ShouldBe(allowed);
        _limits.Released.ShouldBe(allowed ? 0 : 1);
    }

    public void Dispose() => _services.Dispose();

    private sealed class FakeLimits : ILimitsGateway
    {
        public bool Allow { get; set; } = true;

        public int Released { get; private set; }

        public Task<LimitDecisionResult> ReserveAsync(TransactionId transactionId, UserId userId, JournalType type, Money amount, string? deviceId, string? counterparty, CancellationToken cancellationToken) =>
            Task.FromResult(Allow ? new LimitDecisionResult(true, "Allowed", "Within limits.") : new LimitDecisionResult(false, "ExceedsDaily", "Over the daily limit."));

        public Task ReleaseAsync(TransactionId transactionId, CancellationToken cancellationToken)
        {
            Released++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A gRPC channel that answers Score with <see cref="Answer"/>, or fails as if the service were down.</summary>
    private sealed class FakeRisk : CallInvoker
    {
        public ScoreResponse Answer { get; set; } = new() { Action = RiskAction.Allow };

        public bool Fail { get; set; }

        public List<ScoreRequest> Requests { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Requests.Add((ScoreRequest)(object)request);
            var response = Fail
                ? Task.FromException<TResponse>(new RpcException(new Status(StatusCode.Unavailable, "Fraud is down (test).")))
                : Task.FromResult((TResponse)(object)Answer);
            return new AsyncUnaryCall<TResponse>(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
