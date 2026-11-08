using WalletPK.Common.Results;

namespace WalletPK.Identity.Application.Registration;

public static class RegistrationErrors
{
    public static Error AlreadyRegistered { get; } =
        Error.Conflict("identity.phone_already_registered", "This number already has a WalletPK account. Please log in instead.");

    public static Error SessionExpired { get; } =
        Error.Validation("registration.session_expired", "Your registration session has expired. Please verify your number again.");
}
