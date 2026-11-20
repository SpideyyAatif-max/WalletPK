using System.Diagnostics;
using System.Diagnostics.Metrics;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Infrastructure.Risk;

/// <summary>Where the Fraud service's gRPC port is, from "Services:FraudGrpc".</summary>
public sealed class FraudGrpcOptions
{
    public const string SectionName = "Services:FraudGrpc";

    /// <summary>For example http://localhost:5207 (dev) or http://fraud:8081 (compose). Empty: no risk scoring.</summary>
    public Uri? Address { get; set; }

    /// <summary>How long a score may take. The score itself takes a few milliseconds; this covers a slow network.</summary>
    public int TimeoutMilliseconds { get; set; } = 250;

    /// <summary>
    /// When the Fraud service does not answer in time: true lets the payment through (it is still watched
    /// afterwards, from the ledger events), false refuses it.
    /// </summary>
    public bool FailOpen { get; set; } = true;

    public bool Enabled => Address is not null;

    public bool IsValid() => (Address is null || Address.IsAbsoluteUri) && TimeoutMilliseconds >= 10;
}

/// <summary>
/// The limits, then the risk score (M10.4): wraps the Identity limits gateway so that every customer debit
/// that fits the limits is also scored by the Fraud service over gRPC. A "review" or "block" answer gives
/// the limit reservation back and refuses the debit (RiskReview / RiskBlocked); the Fraud service opens
/// the case and, for a block, has the wallets frozen. Metrics: <c>walletpk.ledger.risk.decisions</c> by
/// action, <c>walletpk.ledger.risk.duration</c>.
/// </summary>
internal sealed partial class RiskCheckedLimitsGateway : ILimitsGateway
{
    public const string MeterName = "WalletPK.Ledger.Risk";

    private readonly ILimitsGateway _limits;
    private readonly Contracts.Fraud.V1.Risk.RiskClient _risk;
    private readonly FraudGrpcOptions _options;
    private readonly ILogger<RiskCheckedLimitsGateway> _logger;
    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _duration;

    public RiskCheckedLimitsGateway(
        ILimitsGateway limits,
        Contracts.Fraud.V1.Risk.RiskClient risk,
        FraudGrpcOptions options,
        IMeterFactory meterFactory,
        ILogger<RiskCheckedLimitsGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _limits = limits;
        _risk = risk;
        _options = options;
        _logger = logger;
        var meter = meterFactory.Create(MeterName);
        _decisions = meter.CreateCounter<long>("walletpk.ledger.risk.decisions", description: "Risk scores of customer debits, by action");
        _duration = meter.CreateHistogram<double>("walletpk.ledger.risk.duration", unit: "ms", description: "Time to get a risk score");
    }

    public async Task<LimitDecisionResult> ReserveAsync(
        TransactionId transactionId, UserId userId, JournalType type, Money amount, string? deviceId, string? counterparty, CancellationToken cancellationToken)
    {
        var limits = await _limits.ReserveAsync(transactionId, userId, type, amount, deviceId, counterparty, cancellationToken);
        if (!limits.Allowed)
        {
            return limits;
        }

        var stopwatch = Stopwatch.StartNew();
        ScoreResponse response;
        try
        {
            response = await _risk.ScoreAsync(
                new ScoreRequest
                {
                    TransactionId = transactionId.ToString(),
                    UserId = userId.ToString(),
                    DeviceId = deviceId ?? string.Empty,
                    TransactionType = type.ToString(),
                    AmountPaisa = amount.MinorUnits,
                    Counterparty = counterparty ?? string.Empty,
                },
                deadline: DateTime.UtcNow.AddMilliseconds(_options.TimeoutMilliseconds),
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled || !cancellationToken.IsCancellationRequested)
        {
            Record("Unavailable", stopwatch);
            LogScoreFailed(_logger, exception.StatusCode, transactionId, _options.FailOpen, exception);
            if (_options.FailOpen)
            {
                return limits;
            }

            await _limits.ReleaseAsync(transactionId, CancellationToken.None);
            return new LimitDecisionResult(false, RiskDecisions.Review, "This payment could not be checked right now. Please try again in a moment.");
        }

        Record(response.Action.ToString(), stopwatch);
        switch (response.Action)
        {
            case RiskAction.StepUp:
                await _limits.ReleaseAsync(transactionId, CancellationToken.None);
                LogRefused(_logger, transactionId, response.Action, response.Score, string.Join(',', response.Rules));
                return new LimitDecisionResult(false, RiskDecisions.StepUp, $"{Reason(response, "This payment needs an extra check.")} Confirm it with the code we send to your phone, then send it again.");
            case RiskAction.Review:
                await _limits.ReleaseAsync(transactionId, CancellationToken.None);
                LogRefused(_logger, transactionId, response.Action, response.Score, string.Join(',', response.Rules));
                return new LimitDecisionResult(false, RiskDecisions.Review, Reason(response, "This payment needs a check by our team. We will contact you; your money is safe."));
            case RiskAction.Block:
                await _limits.ReleaseAsync(transactionId, CancellationToken.None);
                LogRefused(_logger, transactionId, response.Action, response.Score, string.Join(',', response.Rules));
                return new LimitDecisionResult(false, RiskDecisions.Blocked, Reason(response, "For your safety your wallet has been frozen. Please call WalletPK support."));
            default:
                return limits;
        }
    }

    public Task ReleaseAsync(TransactionId transactionId, CancellationToken cancellationToken) => _limits.ReleaseAsync(transactionId, cancellationToken);

    private static string Reason(ScoreResponse response, string fallback) => string.IsNullOrWhiteSpace(response.Reason) ? fallback : response.Reason;

    private void Record(string action, Stopwatch stopwatch)
    {
        var tags = new TagList { { "action", action } };
        _decisions.Add(1, tags);
        _duration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Risk score failed with {StatusCode} for transaction {TransactionId}; fail open: {FailOpen}")]
    private static partial void LogScoreFailed(ILogger logger, StatusCode statusCode, TransactionId transactionId, bool failOpen, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transaction {TransactionId} refused by the risk score: {Action} ({Score}; {Rules})")]
    private static partial void LogRefused(ILogger logger, TransactionId transactionId, RiskAction action, int score, string rules);
}
