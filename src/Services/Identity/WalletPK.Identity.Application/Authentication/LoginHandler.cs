using System.Security.Cryptography;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>
/// Phone + MPIN login.
/// <list type="number">
/// <item>Locked or frozen accounts are refused before the MPIN is checked.</item>
/// <item>A wrong MPIN is counted; the 5th in a row locks the account for 30 minutes.</item>
/// <item>On a trusted device the user is signed in at once (access + refresh token).</item>
/// <item>On a new device an OTP is sent and a challenge returned; <see cref="VerifyDeviceHandler"/> finishes the login.</item>
/// </list>
/// </summary>
public sealed class LoginHandler(
    IUserRepository users,
    IDeviceRepository devices,
    IUnitOfWork unitOfWork,
    IMpinHasher mpinHasher,
    OtpService otp,
    IDeviceChallengeStore challenges,
    SessionIssuer sessions,
    AuthOptions options,
    TimeProvider timeProvider)
{
    public async Task<Result<LoginOutcome>> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var phone = PhoneNumber.Create(command.PhoneNumber);
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        var device = DeviceInput.Create(command.InstallationId, command.DeviceName);
        if (device.IsFailure)
        {
            return device.Error;
        }

        if (string.IsNullOrEmpty(command.Mpin))
        {
            return AuthErrors.InvalidCredentials;
        }

        var now = timeProvider.GetUtcNow();
        var user = await users.GetByPhoneAsync(phone.Value, cancellationToken);
        if (user is null)
        {
            // Spend the same time as a real check, so response timing does not reveal which numbers have accounts.
            _ = mpinHasher.Hash(command.Mpin);
            return AuthErrors.InvalidCredentials;
        }

        var allowed = user.EnsureCanLogIn(now);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        if (!mpinHasher.Verify(command.Mpin, user.MpinHash))
        {
            return await RejectWrongMpinAsync(user, now, cancellationToken);
        }

        var known = await devices.GetByInstallationAsync(user.Id, device.Value.InstallationId, cancellationToken);
        if (known is { IsTrusted: true })
        {
            known.MarkSeen(now);
            user.RecordSuccessfulLogin(now);
            var tokens = sessions.Start(user, known, now);

            var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
            return saved.IsFailure ? saved.Error : LoginOutcome.SignedIn(tokens);
        }

        return await ChallengeNewDeviceAsync(user, device.Value, cancellationToken);
    }

    private async Task<Result<LoginOutcome>> RejectWrongMpinAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lockedNow = user.RecordFailedMpin(now, options.MaxFailedMpinAttempts, options.LockoutDuration);

        // xmin makes parallel wrong guesses conflict, so the attempt counter cannot be bypassed by sending them at once.
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        return lockedNow
            ? AuthErrors.LockedNow(options.LockoutMinutes)
            : AuthErrors.IncorrectMpin(options.MaxFailedMpinAttempts - user.FailedMpinAttempts);
    }

    private async Task<Result<LoginOutcome>> ChallengeNewDeviceAsync(User user, DeviceInput device, CancellationToken cancellationToken)
    {
        // The MPIN was right; a lockout that just expired may have been lifted, so keep that change.
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        // Owning the SIM is the second factor: an OTP goes to the registered number.
        var issued = await otp.IssueAsync(OtpPurpose.DeviceVerification, user.PhoneNumber, cancellationToken);
        if (issued.IsFailure)
        {
            return issued.Error;
        }

        var challengeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await challenges.SaveAsync(
            challengeId,
            new DeviceChallenge(user.Id, device.InstallationId, device.Name),
            options.DeviceChallengeLifetime,
            cancellationToken);

        return LoginOutcome.NeedsDeviceVerification(new DeviceVerificationChallenge(
            challengeId, user.PhoneNumber.Masked, issued.Value.ExpiresInSeconds, issued.Value.ResendAfterSeconds));
    }
}
