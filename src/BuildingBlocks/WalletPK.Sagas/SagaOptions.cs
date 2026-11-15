namespace WalletPK.Sagas;

/// <summary>Timer settings, from "Sagas:Timers".</summary>
public sealed class SagaTimerOptions
{
    public const string SectionName = "Sagas:Timers";

    /// <summary>Run the background timer loop (tests may switch it off and run timers on demand).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Due Sagas taken per round.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Timer steps run at once (each may wait on an outside system).</summary>
    public int MaxParallelism { get; set; } = 16;

    /// <summary>Wait between rounds when nothing (or not a full batch) was due.</summary>
    public int PollIntervalMilliseconds { get; set; } = 250;

    /// <summary>
    /// A taken Saga is not taken again (by this or another instance) for this long. If the instance
    /// dies mid-step, another one repeats the step once the lease runs out.
    /// </summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>A timer step is cancelled after this long (must be shorter than the lease).</summary>
    public int StepTimeoutSeconds { get; set; } = 45;

    /// <summary>A step that threw is retried after this long.</summary>
    public int RetryDelaySeconds { get; set; } = 10;

    public bool IsValid() =>
        BatchSize is >= 1 and <= 1_000
        && MaxParallelism is >= 1 and <= 256
        && PollIntervalMilliseconds >= 10
        && StepTimeoutSeconds >= 1
        && LeaseSeconds > StepTimeoutSeconds
        && RetryDelaySeconds >= 1;
}
