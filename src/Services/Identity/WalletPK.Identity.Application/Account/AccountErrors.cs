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

    public static Error BindingRequired { get; } =
        Error.Validation("identity.confirmation_binding_required", "Give the payment's Idempotency-Key (at most 100 characters) to confirm it.");

    public static Error DeviceRequired { get; } =
        Error.Forbidden("identity.device_required", "Sign in on this device again to confirm payments.");

    /// <summary>400, not 401: the session is fine, only the MPIN typed was wrong.</summary>
    public static Error IncorrectMpin(int attemptsLeft) =>
        Error.Validation(
            "identity.incorrect_mpin",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Incorrect MPIN. {attemptsLeft} attempt(s) left before your account is locked."));
}
