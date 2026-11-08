using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>
/// Finishes a login on a new device: checks the OTP, trusts the device and signs the user in.
/// If the user already has another device, the new one starts its cooling-off period.
/// </summary>
public sealed class VerifyDeviceHandler(
    IDeviceChallengeStore challenges,
    IUserRepository users,
    IDeviceRepository devices,
    IUnitOfWork unitOfWork,
    OtpService otp,
    SessionIssuer sessions,
    AuthOptions options,
    TimeProvider timeProvider)
{
    public async Task<Result<AuthTokens>> HandleAsync(VerifyDeviceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ChallengeId))
        {
            return AuthErrors.ChallengeExpired;
        }

        var challenge = await challenges.GetAsync(command.ChallengeId, cancellationToken);
        if (challenge is null)
        {
            return AuthErrors.ChallengeExpired;
        }

        var user = await users.GetByIdAsync(challenge.UserId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.ChallengeExpired;
        }

        var now = timeProvider.GetUtcNow();
        var allowed = user.EnsureCanLogIn(now);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        // OtpService enforces expiry, the attempt limit and single use.
        var verified = await otp.VerifyAsync(OtpPurpose.DeviceVerification, user.PhoneNumber, command.Code, cancellationToken);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        await challenges.DeleteAsync(command.ChallengeId, cancellationToken);

        // The first phone a user ever uses is trusted fully; any later one starts a cooling-off period.
        TimeSpan? coolingOff = await devices.HasOtherDevicesAsync(user.Id, challenge.InstallationId, cancellationToken)
            ? options.NewDeviceCoolingOff
            : null;

        var device = await devices.GetByInstallationAsync(user.Id, challenge.InstallationId, cancellationToken);
        if (device is null)
        {
            device = Device.Trust(user.Id, challenge.InstallationId, challenge.DeviceName, now, coolingOff);
            devices.Add(device);
        }
        else if (!device.IsTrusted)
        {
            device.TrustAgain(challenge.DeviceName, now, coolingOff);
        }
        else
        {
            device.MarkSeen(now);
        }

        user.RecordSuccessfulLogin(now);
        var tokens = sessions.Start(user, device, now);

        // A unique-index clash here means the same device was verified twice at once; the other request won.
        var saved = await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
        return saved.IsFailure ? saved.Error : tokens;
    }
}
