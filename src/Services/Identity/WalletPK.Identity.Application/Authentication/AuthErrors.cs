using System.Globalization;
using WalletPK.Common.Results;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Authentication;

public static class AuthErrors
{
    public const string InvalidCredentialsCode = "auth.invalid_credentials";

    /// <summary>Same answer for an unknown number and a wrong MPIN.</summary>
    public static Error InvalidCredentials { get; } =
        Error.Unauthorized(InvalidCredentialsCode, "Incorrect mobile number or MPIN.");

    public static Error InvalidDevice { get; } =
        Error.Validation("auth.invalid_device", "The app did not send a valid device ID. Please update or reinstall the app.");

    public static Error ChallengeExpired { get; } =
        Error.Validation("auth.device_challenge_expired", "This verification has expired. Please log in again.");

    public static Error InvalidRefreshToken { get; } =
        Error.Unauthorized("auth.invalid_refresh_token", "Your session has expired. Please log in again.");

    public static Error RefreshTokenReused { get; } =
        Error.Unauthorized("auth.refresh_token_reused", "For your security this session was ended because it was used from another place. Please log in again.");

    public static Error ConcurrentRequest { get; } =
        Error.Conflict("auth.concurrent_request", "Another request changed this account at the same moment. Please try again.");

    public static Error IncorrectMpin(int attemptsLeft) =>
        Error.Unauthorized(
            InvalidCredentialsCode,
            string.Create(CultureInfo.InvariantCulture, $"Incorrect mobile number or MPIN. {attemptsLeft} attempt(s) left before your account is locked."));

    public static Error LockedNow(int minutes) =>
        Error.Forbidden(
            UserErrors.LockedCode,
            string.Create(CultureInfo.InvariantCulture, $"Too many incorrect MPINs. Your account is locked for {minutes} minute(s)."));
}
