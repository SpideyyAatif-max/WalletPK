namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// Know-Your-Customer level, modelled on branchless-banking account tiers.
/// Higher tiers need more verification and allow higher transaction limits (limits are configuration, M2.6).
/// </summary>
public enum KycTier
{
    /// <summary>Phone number verified only. Small limits.</summary>
    L0 = 0,

    /// <summary>CNIC verified against NADRA.</summary>
    L1 = 1,

    /// <summary>Full verification (biometric / documents). Highest limits.</summary>
    L2 = 2,
}
