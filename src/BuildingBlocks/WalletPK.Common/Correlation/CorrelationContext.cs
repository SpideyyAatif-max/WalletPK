namespace WalletPK.Common.Correlation;

/// <summary>
/// The correlation ID of the operation currently running on this async flow.
/// Set by the CorrelationIdMiddleware for HTTP requests and by the Kafka consumer host for each
/// message (from its correlation-id header), read by logging, outgoing HTTP calls and outbox messages,
/// so one user action can be followed across every service it touches.
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The current correlation ID, or null outside a request or message.</summary>
    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
