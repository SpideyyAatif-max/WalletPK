namespace WalletPK.MockProviders;

/// <summary>Settings from the "MockProviders" section.</summary>
public sealed class MockProvidersOptions
{
    public const string SectionName = "MockProviders";

    /// <summary>Keys WalletPK services send as X-Api-Key when they call a provider.</summary>
    public List<string> ClientApiKeys { get; } = [];

    /// <summary>Key for the control API (/admin/*): faults, settlement mismatches, NADRA records. Sent as X-Admin-Key.</summary>
    public string AdminApiKey { get; set; } = "";

    public RaastOptions Raast { get; set; } = new();

    public CallbackOptions Callbacks { get; set; } = new();

    public NadraOptions Nadra { get; set; } = new();

    public bool IsValid() =>
        ClientApiKeys.Count > 0
        && ClientApiKeys.All(key => key.Length >= 16)
        && AdminApiKey.Length >= 16
        && Raast.SettleDelayMinMilliseconds >= 0
        && Raast.SettleDelayMaxMilliseconds >= Raast.SettleDelayMinMilliseconds
        && Callbacks.MaxAttempts is >= 1 and <= 50
        && Callbacks.PollMilliseconds >= 10;
}

public sealed class RaastOptions
{
    /// <summary>
    /// How long the beneficiary bank takes to settle an accepted transfer (a random time in this range).
    /// The confirmation then arrives as a signed callback.
    /// </summary>
    public int SettleDelayMinMilliseconds { get; set; } = 300;

    public int SettleDelayMaxMilliseconds { get; set; } = 1_500;

    /// <summary>Where transfer confirmations are POSTed (WalletPK's webhook, through the gateway). Empty: no callbacks.</summary>
    public string CallbackUrl { get; set; } = "";

    /// <summary>HMAC secret shared with the gateway's Webhooks:Secrets:raast.</summary>
    public string CallbackSecret { get; set; } = "";
}

public sealed class CallbackOptions
{
    /// <summary>Deliveries are retried with exponential back-off (1 s, 2 s, 4 s ... at most 5 min) this many times.</summary>
    public int MaxAttempts { get; set; } = 12;

    public int PollMilliseconds { get; set; } = 250;

    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class NadraOptions
{
    /// <summary>
    /// A well-formed CNIC that NADRA has no record of (and no test prefix) verifies, so any made-up
    /// identity works in demos. Records added through /admin/nadra/citizens are checked strictly.
    /// </summary>
    public bool VerifyUnknownCitizens { get; set; } = true;
}
