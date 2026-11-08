namespace WalletPK.Web.Identity;

/// <summary>
/// Headers the API gateway adds to proxied requests after validating the access token.
/// The gateway always strips these from incoming client requests first, so a client cannot
/// impersonate another user. They are informational: services still validate the token itself.
/// </summary>
public static class IdentityHeaders
{
    public const string UserId = "X-WalletPK-User-Id";
    public const string DeviceId = "X-WalletPK-Device-Id";
    public const string KycTier = "X-WalletPK-Kyc-Tier";
    public const string Roles = "X-WalletPK-Roles";

    public static IReadOnlyList<string> All { get; } = [UserId, DeviceId, KycTier, Roles];
}
