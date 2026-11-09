namespace WalletPK.Web.Authentication;

/// <summary>
/// How WalletPK services (the gateway and every API) validate access tokens issued by the Identity service.
/// Signing keys are discovered from <c>{Authority}/.well-known/openid-configuration</c>, so the
/// gateway never holds a signing secret and keys can rotate without redeploying it.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Base URL of the Identity service (token issuer).</summary>
    public Uri Authority { get; set; } = new("http://localhost:5101/");

    public string Issuer { get; set; } = "walletpk-identity";

    public string Audience { get; set; } = "walletpk-api";

    /// <summary>Must be true outside local development.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Tolerance for small clock differences between servers.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
