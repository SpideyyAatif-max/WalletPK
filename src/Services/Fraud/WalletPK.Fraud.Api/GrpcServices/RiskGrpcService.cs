using System.Diagnostics;
using System.Diagnostics.Metrics;
using Grpc.Core;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Fraud.Application;
using DomainAction = WalletPK.Fraud.Domain.RiskAction;

namespace WalletPK.Fraud.Api.GrpcServices;

/// <summary>Score metrics: <c>walletpk.fraud.score.duration</c> (ms) and <c>walletpk.fraud.decisions</c> by action.</summary>
public sealed class ScoreMetrics
{
    public const string MeterName = "WalletPK.Fraud";

    private readonly Histogram<double> _duration;
    private readonly Counter<long> _decisions;

    public ScoreMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _duration = meter.CreateHistogram<double>("walletpk.fraud.score.duration", unit: "ms", description: "Time to score a payment (target: under 20 ms)");
        _decisions = meter.CreateCounter<long>("walletpk.fraud.decisions", description: "Risk decisions, by action");
    }

    public void Record(DomainAction action, TimeSpan elapsed)
    {
        var tags = new TagList { { "action", action.ToString() } };
        _duration.Record(elapsed.TotalMilliseconds, tags);
        _decisions.Add(1, tags);
    }
}

/// <summary>
/// The gRPC face of <see cref="RiskScorer"/> (M10.4), for the Ledger before every customer debit. Internal
/// only: on the gRPC port, which the gateway never routes to. Bad input is INVALID_ARGUMENT.
/// </summary>
public sealed class RiskGrpcService(RiskScorer scorer, ScoreMetrics metrics) : Risk.RiskBase
{
    public override async Task<ScoreResponse> Score(ScoreRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!Guid.TryParse(request.TransactionId, out var transactionId) || !Guid.TryParse(request.UserId, out var userId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "transaction_id and user_id must be GUIDs."));
        }

        if (request.AmountPaisa <= 0 || string.IsNullOrWhiteSpace(request.TransactionType))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "amount_paisa must be positive and transaction_type set."));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await scorer.ScoreAsync(
            new ScoreInput(
                transactionId,
                userId,
                string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId,
                request.TransactionType,
                request.AmountPaisa,
                string.IsNullOrWhiteSpace(request.Counterparty) ? null : request.Counterparty),
            context.CancellationToken);
        metrics.Record(result.Action, stopwatch.Elapsed);

        var response = new ScoreResponse
        {
            Action = result.Action switch
            {
                DomainAction.Block => RiskAction.Block,
                DomainAction.Review => RiskAction.Review,
                DomainAction.StepUp => RiskAction.StepUp,
                _ => RiskAction.Allow,
            },
            Score = result.Score,
            Reason = result.Reason ?? string.Empty,
        };
        response.Rules.AddRange(result.Rules);
        return response;
    }
}
