using System.Diagnostics.Metrics;
using WalletPK.Ledger.Infrastructure.Invariants;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>Projection metrics: events applied, how far behind the ledger they were, and history gaps.</summary>
public sealed class ReadModelMetrics
{
    private readonly Counter<long> _projected;
    private readonly Histogram<double> _lag;
    private readonly Counter<long> _gaps;

    public ReadModelMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(LedgerMetrics.MeterName);
        _projected = meter.CreateCounter<long>("walletpk.ledger.read_model.projected", description: "Events applied to the read models, by event type and result");
        _lag = meter.CreateHistogram<double>("walletpk.ledger.read_model.lag", unit: "ms", description: "Time from an event happening to it reaching the read models");
        _gaps = meter.CreateCounter<long>("walletpk.ledger.read_model.gaps", description: "Wallet history gaps seen by the projection (entries that never arrived)");
    }

    public void Projected(string eventType, string result, TimeSpan lag)
    {
        _projected.Add(1, new KeyValuePair<string, object?>("event_type", eventType), new KeyValuePair<string, object?>("result", result));
        if (result == "applied")
        {
            _lag.Record(Math.Max(0, lag.TotalMilliseconds), new KeyValuePair<string, object?>("event_type", eventType));
        }
    }

    public void Gap() => _gaps.Add(1);
}
