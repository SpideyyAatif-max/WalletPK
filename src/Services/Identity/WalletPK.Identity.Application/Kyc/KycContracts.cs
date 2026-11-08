using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Kyc;

/// <summary>Upgrade from L0 to L1 by verifying a CNIC with NADRA.</summary>
public sealed record SubmitKycCommand(string? Cnic, string? FullName, DateOnly? DateOfBirth);

/// <summary>The user's verification state (GET /api/identity/kyc and the reply to a submission).</summary>
/// <param name="KycTier">Current tier.</param>
/// <param name="Verified">True once the CNIC has been verified.</param>
/// <param name="CnicMasked">For example "35202-*******-1"; null until verified.</param>
/// <param name="FullName">Name as verified by NADRA; null until verified.</param>
/// <param name="VerifiedAt">When NADRA confirmed the identity.</param>
public sealed record KycStatus(KycTier KycTier, bool Verified, string? CnicMasked, string? FullName, DateTimeOffset? VerifiedAt);
