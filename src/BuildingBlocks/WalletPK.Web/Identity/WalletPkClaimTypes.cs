namespace WalletPK.Web.Identity;

/// <summary>Claim names inside WalletPK access tokens (issued by the Identity service).</summary>
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

    /// <summary>The user's mobile number in E.164 (standard OpenID Connect claim); it is also their wallet number.</summary>
    public const string PhoneNumber = "phone_number";

    /// <summary>
    /// Present only while the device is in its new-device cooling-off period: the Unix time (seconds)
    /// when it ends. Services limit high-risk actions (large transfers) until then.
    /// </summary>
    public const string CoolingOffUntil = "cooling_off_until";
}
