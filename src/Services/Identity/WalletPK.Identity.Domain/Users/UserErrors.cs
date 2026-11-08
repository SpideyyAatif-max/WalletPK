using WalletPK.Common.Results;

namespace WalletPK.Identity.Domain.Users;

public static class UserErrors
{
    public const string LockedCode = "identity.account_locked";

    public static Error Frozen { get; } =
        Error.Forbidden("identity.account_frozen", "This account is on hold. Please contact WalletPK support.");

    public static Error AlreadyAtTier(KycTier tier) =>
        Error.Conflict("kyc.already_verified", $"This account is already verified at level {tier}.");
}
