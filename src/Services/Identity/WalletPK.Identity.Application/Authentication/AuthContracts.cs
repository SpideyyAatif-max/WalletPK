using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>Log in with phone number and MPIN from a device identified by its installation ID.</summary>
public sealed record LoginCommand(string? PhoneNumber, string? Mpin, string? InstallationId, string? DeviceName);

/// <summary>Finish a login on a new device by entering the OTP sent to the user's number.</summary>
public sealed record VerifyDeviceCommand(string? ChallengeId, string? Code);

public sealed record RefreshSessionCommand(string? RefreshToken);

public sealed record LogoutCommand(string? RefreshToken);

/// <summary>A signed-in session.</summary>
/// <param name="AccessToken">JWT to send as "Authorization: Bearer ..." on every API call.</param>
/// <param name="TokenType">Always "Bearer".</param>
/// <param name="ExpiresIn">Seconds until the access token expires.</param>
/// <param name="RefreshToken">Single-use secret that buys the next access token; store it securely.</param>
/// <param name="RefreshTokenExpiresIn">Seconds until the refresh token expires if unused.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="DeviceId">The trusted device this session belongs to.</param>
/// <param name="CoolingOffUntil">Set while the device is in its new-device cooling-off period.</param>
public sealed record AuthTokens(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    int RefreshTokenExpiresIn,
    UserId UserId,
    DeviceId DeviceId,
    DateTimeOffset? CoolingOffUntil);

/// <summary>The MPIN was right but the device is new: an OTP was sent and must be entered.</summary>
/// <param name="ChallengeId">Send this back with the OTP.</param>
/// <param name="PhoneNumber">Masked number the OTP was sent to.</param>
/// <param name="ExpiresInSeconds">How long the OTP stays valid.</param>
/// <param name="ResendAfterSeconds">How long until another OTP may be requested (by logging in again).</param>
public sealed record DeviceVerificationChallenge(string ChallengeId, string PhoneNumber, int ExpiresInSeconds, int ResendAfterSeconds);

/// <summary>Either a session (known device) or a device challenge (new device), never both.</summary>
public sealed record LoginOutcome
{
    private LoginOutcome(AuthTokens? tokens, DeviceVerificationChallenge? challenge)
    {
        Tokens = tokens;
        Challenge = challenge;
    }

    public AuthTokens? Tokens { get; }

    public DeviceVerificationChallenge? Challenge { get; }

    public static LoginOutcome SignedIn(AuthTokens tokens) => new(tokens ?? throw new ArgumentNullException(nameof(tokens)), null);

    public static LoginOutcome NeedsDeviceVerification(DeviceVerificationChallenge challenge) =>
        new(null, challenge ?? throw new ArgumentNullException(nameof(challenge)));
}
