using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WalletPK.Sagas;

/// <summary>ActivitySource and Meter "WalletPK.Sagas" (picked up by WalletPK's observability wiring).</summary>
internal static class SagaTelemetry
{
    public const string Name = "WalletPK.Sagas";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>Sagas started, by type.</summary>
    public static readonly Counter<long> Started = Meter.CreateCounter<long>("walletpk.sagas.started");

    /// <summary>State changes, by type and new state.</summary>
    public static readonly Counter<long> Transitions = Meter.CreateCounter<long>("walletpk.sagas.transitions");

    /// <summary>Sagas that finished, by type and final state.</summary>
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("walletpk.sagas.completed");

    /// <summary>Timer steps, by type and outcome (applied, stale, failed).</summary>
    public static readonly Counter<long> TimerSteps = Meter.CreateCounter<long>("walletpk.sagas.timer_steps");

    /// <summary>How long timer steps take (they include calls to outside systems).</summary>
    public static readonly Histogram<double> TimerStepDuration = Meter.CreateHistogram<double>("walletpk.sagas.timer_step.duration", unit: "s");

    /// <summary>How late timers fire (due time to start of the step).</summary>
    public static readonly Histogram<double> TimerLag = Meter.CreateHistogram<double>("walletpk.sagas.timer.lag", unit: "s");
}
