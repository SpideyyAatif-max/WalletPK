using System.Text.Json.Serialization;

namespace WalletPK.MockProviders.Faults;

/// <summary>How an outage looks to the caller.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutageMode>))]
public enum OutageMode
{
    /// <summary>Every call gets 503 straight away.</summary>
    Unavailable = 0,

    /// <summary>Every call has its connection dropped without a response (like a crashed server).</summary>
    ConnectionReset = 1,
}

/// <summary>
/// Faults for one provider, set at runtime through PUT /admin/faults/{provider} (M5.1). The rates are
/// chances per call from 0 to 1 and are checked in this order: error, timeout, accept-but-timeout.
/// </summary>
/// <param name="LatencyMs">Extra delay before every answer.</param>
/// <param name="JitterMs">Up to this much more, at random.</param>
/// <param name="ErrorRate">Chance of a 500 instead of an answer (nothing happens).</param>
/// <param name="TimeoutRate">Chance the call hangs for <paramref name="TimeoutMs"/> and then gets 504 (nothing happens).</param>
/// <param name="AcceptButTimeoutRate">
/// Chance the provider DOES the work (the payment goes through) but never answers: the connection hangs
/// for <paramref name="TimeoutMs"/> and is then dropped (M5.7). Only a status query tells what happened.
/// </param>
/// <param name="Outage">Fully down: nothing gets through.</param>
/// <param name="OutageMode">How the outage looks.</param>
/// <param name="TimeoutMs">How long a timed-out call hangs.</param>
/// <param name="DurationSeconds">Remove these faults automatically after this long (null: until cleared).</param>
public sealed record FaultSettings(
    int LatencyMs = 0,
    int JitterMs = 0,
    double ErrorRate = 0,
    double TimeoutRate = 0,
    double AcceptButTimeoutRate = 0,
    bool Outage = false,
    OutageMode OutageMode = OutageMode.Unavailable,
    int TimeoutMs = 30_000,
    int? DurationSeconds = null)
{
    /// <summary>What is wrong with these settings, or null when they are usable.</summary>
    public string? Problem() =>
        LatencyMs is < 0 or > 120_000 ? "latencyMs must be 0-120000."
        : JitterMs is < 0 or > 120_000 ? "jitterMs must be 0-120000."
        : TimeoutMs is < 1 or > 600_000 ? "timeoutMs must be 1-600000."
        : ErrorRate is < 0 or > 1 || TimeoutRate is < 0 or > 1 || AcceptButTimeoutRate is < 0 or > 1 ? "Rates must be between 0 and 1."
        : ErrorRate + TimeoutRate + AcceptButTimeoutRate > 1 ? "errorRate + timeoutRate + acceptButTimeoutRate must not exceed 1."
        : DurationSeconds is <= 0 ? "durationSeconds must be positive."
        : null;
}

/// <summary>Faults in force for a provider, with when they end.</summary>
public sealed record ActiveFaults(string Provider, FaultSettings Settings, DateTimeOffset SetAt, DateTimeOffset? ExpiresAt);
