using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WalletPK.Fraud.Domain;

namespace WalletPK.Fraud.Application;

/// <summary>A payment to a wallet, for ring detection: who paid whom, when.</summary>
public sealed record RingObservation(string Recipient, Guid SenderUserId, DateTimeOffset At);

/// <summary>
/// Ring observations waiting for the detector (M10.5): the scorer adds them without waiting, so detection
/// never slows a payment down; a background service works through them.
/// </summary>
public sealed class RingQueue
{
    private readonly Channel<RingObservation> _channel = Channel.CreateBounded<RingObservation>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public ChannelReader<RingObservation> Reader => _channel.Reader;

    public void Add(RingObservation observation) => _channel.Writer.TryWrite(observation);
}

/// <summary>Builds the feature vector for one payment from what Redis knows (M10.1).</summary>
public static class FeatureBuilder
{
    public static FeatureVector Build(ScoreInput input, FeatureSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(snapshot);

        int Count(TimeSpan window) => snapshot.Recent.Count(payment => payment.At > now - window) + 1;
        double Sum(TimeSpan window) => snapshot.Recent.Where(payment => payment.At > now - window).Sum(payment => (double)payment.AmountPaisa) + input.AmountPaisa;
        static double Hours(DateTimeOffset? since, DateTimeOffset now) => since is { } at ? Math.Max(0, (now - at).TotalHours) : FeatureCatalog.UnknownAgeHours;

        return new FeatureVector()
            .Set(FeatureCatalog.AmountPaisa, input.AmountPaisa)
            .Set(FeatureCatalog.TransactionType, input.TransactionType)
            .Set(FeatureCatalog.HourPkt, RiskPolicy.HourInPakistan(now))
            .Set(FeatureCatalog.AccountAgeHours, Hours(snapshot.RegisteredAt, now))
            .Set(FeatureCatalog.DeviceAgeHours, Hours(snapshot.DeviceBoundAt, now))
            .Set(FeatureCatalog.DeviceCoolingOff, snapshot.CoolingOffUntil > now)
            .Set(FeatureCatalog.RecipientKnown, input.Counterparty is null || snapshot.RecipientKnown)
            .Set(FeatureCatalog.Count60s, Count(TimeSpan.FromSeconds(60)))
            .Set(FeatureCatalog.Count1h, Count(TimeSpan.FromHours(1)))
            .Set(FeatureCatalog.Count24h, Count(TimeSpan.FromHours(24)))
            .Set(FeatureCatalog.Sum1hPaisa, Sum(TimeSpan.FromHours(1)))
            .Set(FeatureCatalog.Sum24hPaisa, Sum(TimeSpan.FromHours(24)))
            .Set(FeatureCatalog.RecipientSenders1h, input.Counterparty is null ? 0 : snapshot.RecipientSenders1h + 1)
            .Set(FeatureCatalog.FlaggedRecently, snapshot.FlaggedRecently);
    }
}

/// <summary>
/// Scores one payment (M10.4): features from Redis, the rules in memory, one answer. Only payments worth an
/// alert touch the database, so an ordinary payment is scored in a few milliseconds. A retry of the same
/// transaction gets the first answer and counts nothing twice.
/// </summary>
public sealed partial class RiskScorer(
    IFeatureStore features,
    IRuleCache rules,
    RingQueue rings,
    AlertService alerts,
    FraudOptions options,
    TimeProvider timeProvider,
    ILogger<RiskScorer> logger)
{
    public async Task<RiskResult> ScoreAsync(ScoreInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await features.CachedAsync(input.TransactionId, cancellationToken) is { } earlier)
        {
            return earlier;
        }

        var now = timeProvider.GetUtcNow();
        var snapshot = await features.ReadAsync(input, now, cancellationToken);
        var vector = FeatureBuilder.Build(input, snapshot, now);
        var result = options.Policy().Evaluate(rules.Rules, vector);
        var usedStepUp = result.Action == RiskAction.StepUp && snapshot.StepUpConfirmed;
        if (usedStepUp)
        {
            result = RiskPolicy.Confirmed(result);
        }

        await features.RecordAsync(input, result, now, usedStepUp);
        if (input is { TransactionType: "P2PTransfer", Counterparty: { } recipient })
        {
            rings.Add(new RingObservation(recipient, input.UserId, now));
        }

        if (result.Action is RiskAction.Review or RiskAction.Block || result.Score >= options.AlertAt)
        {
            // The decision is made and cached: its alert, case and any freeze are saved even if the
            // caller has given up waiting (a Ledger past its deadline fails open, so the alert matters more).
            // If saving them fails, the decision still stands: an error here would make the Ledger fail open
            // and let a blocked payment through.
            try
            {
                await alerts.PaymentAsync(input, result, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogAlertFailed(logger, input.TransactionId, result.Action, exception);
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the alert for transaction {TransactionId} ({Action}) failed; the decision stands")]
    private static partial void LogAlertFailed(ILogger logger, Guid transactionId, RiskAction action, Exception exception);

    /// <summary>What the rules would say about <paramref name="vector"/> (the rule editor's dry run).</summary>
    public RiskResult Explain(FeatureVector vector) => options.Policy().Evaluate(rules.Rules, vector);
}
