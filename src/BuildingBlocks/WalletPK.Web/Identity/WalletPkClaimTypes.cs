namespace WalletPK.Web.Identity;

/// <summary>Claim names inside WalletPK access tokens (issued by the Identity service in M2).</summary>
public static class WalletPkClaimTypes
{
    /// <summary>The user ID (standard JWT "sub" claim).</summary>
    public const string Subject = "sub";

    /// <summary>The device the token was issued to (device binding).</summary>
    public const string DeviceId = "device_id";

    /// <summary>The user's KYC tier: L0, L1 or L2 (decides transaction limits).</summary>
    public const string KycTier = "kyc_tier";

    /// <summary>Role: customer, merchant, agent, ops, compliance, finance or admin.</summary>
    public const string Role = "role";
}
