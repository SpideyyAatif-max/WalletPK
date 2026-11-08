namespace WalletPK.Observability;

/// <summary>
/// Naming convention for WalletPK's own traces and metrics. Every service names its
/// ActivitySource and Meter "WalletPK.&lt;Component&gt;" (for example "WalletPK.Ledger"),
/// and the wildcard below makes sure all of them are collected without extra setup.
/// </summary>
public static class WalletPkTelemetry
{
    public const string Prefix = "WalletPK";

    /// <summary>Matches every WalletPK ActivitySource and Meter.</summary>
    public const string Wildcard = Prefix + ".*";

    /// <summary>The ActivitySource or Meter name for a component, for example <c>NameFor("Ledger")</c> = "WalletPK.Ledger".</summary>
    public static string NameFor(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        return $"{Prefix}.{component}";
    }
}
