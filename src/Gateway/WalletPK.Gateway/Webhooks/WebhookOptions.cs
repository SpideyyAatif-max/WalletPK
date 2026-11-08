namespace WalletPK.Gateway.Webhooks;

/// <summary>Webhook verification settings, from the "Webhooks" configuration section.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Requests under this path must carry a valid HMAC signature.</summary>
    public const string PathPrefix = "/webhooks";

    /// <summary>Maximum age (and clock difference) of a signed request; older requests are treated as replays.</summary>
    public int ToleranceSeconds { get; set; } = 300;

    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>Shared secret per provider, keyed by the path segment: /webhooks/{provider}/...</summary>
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);
}
