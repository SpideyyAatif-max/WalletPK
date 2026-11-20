using WalletPK.Common.Results;

namespace WalletPK.Identity.Application.Account;

public static class AccountErrors
{
    public static Error UserNotFound { get; } =
        Error.NotFound("identity.user_not_found", "This account no longer exists.");

    /// <summary>Also returned for another user's device, so device IDs cannot be probed.</summary>
    public static Error DeviceNotFound { get; } =
        Error.NotFound("identity.device_not_found", "Device not found.");

    public static Error CannotTransact { get; } =
        Error.Forbidden("identity.cannot_transact", "This account cannot make payments right now.");
}
