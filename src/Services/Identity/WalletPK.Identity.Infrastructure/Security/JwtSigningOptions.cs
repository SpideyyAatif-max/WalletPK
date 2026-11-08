namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>Access-token signing settings, from the "Jwt" configuration section.</summary>
public sealed class JwtSigningOptions
{
    public const string SectionName = "Jwt";

    /// <summary>The "iss" claim. Must match the gateway's Jwt:Issuer.</summary>
    public string Issuer { get; set; } = "walletpk-identity";

    /// <summary>The "aud" claim. Must match the gateway's Jwt:Audience.</summary>
    public string Audience { get; set; } = "walletpk-api";

    /// <summary>
    /// PEM file holding the RSA private key (relative paths are under the app folder).
    /// In production this is a mounted secret; empty means a temporary in-memory key (tests).
    /// </summary>
    public string SigningKeyPath { get; set; } = string.Empty;

    /// <summary>Development only: create a new 2048-bit key (and save it to SigningKeyPath) when none exists.</summary>
    public bool CreateSigningKeyIfMissing { get; set; }
}
