namespace WalletPK.Web.Correlation;

/// <summary>
/// The correlation ID of the operation currently running on this async flow.
/// Set by <see cref="CorrelationIdMiddleware"/> for HTTP requests (and later by Kafka consumers),
/// read by logging, outgoing HTTP calls and outbox messages.
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
