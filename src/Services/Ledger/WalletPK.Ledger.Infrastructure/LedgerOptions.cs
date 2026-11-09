namespace WalletPK.Ledger.Infrastructure;

/// <summary>Wallet lock settings, from "Ledger:Locks".</summary>
public sealed class WalletLockOptions
{
    public const string SectionName = "Ledger:Locks";

    /// <summary>A lock expires by itself after this long, so a crashed server never blocks a wallet forever.</summary>
    public int TimeToLiveMilliseconds { get; set; } = 10_000;

    /// <summary>How long a payment waits for a busy wallet before giving up with "wallet busy".</summary>
    public int WaitMilliseconds { get; set; } = 3_000;
}

/// <summary>Idempotency settings, from "Ledger:Idempotency".</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Ledger:Idempotency";

    /// <summary>How long a completed response is kept for replays.</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>After this long, an unfinished request (its server crashed) may be taken over by a retry.</summary>
    public int InProgressTimeoutSeconds { get; set; } = 60;
}

/// <summary>Background maintenance settings, from "Ledger:Maintenance".</summary>
public sealed class MaintenanceOptions
{
    public const string SectionName = "Ledger:Maintenance";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Most expired holds released per run.</summary>
    public int HoldBatchSize { get; set; } = 200;
}

/// <summary>Audit chain settings, from "Ledger:Audit".</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Ledger:Audit";

    /// <summary>Run the background sealer (tests switch it off and seal on demand).</summary>
    public bool SealingEnabled { get; set; } = true;

    public int SealIntervalMilliseconds { get; set; } = 2_000;

    public int BatchSize { get; set; } = 500;

    /// <summary>An audit row still unsealed after this long fails the invariant check (the sealer is stuck).</summary>
    public int MaxUnsealedAgeSeconds { get; set; } = 300;
}

/// <summary>Invariant checker settings, from "Ledger:Invariants".</summary>
public sealed class InvariantOptions
{
    public const string SectionName = "Ledger:Invariants";

    /// <summary>Run the checks on a schedule (the endpoint works either way).</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 15;
}
