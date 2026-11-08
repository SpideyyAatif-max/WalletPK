using WalletPK.Common.Identifiers;
using WalletPK.Common.Results;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Account;

/// <summary>Returns the signed-in user's profile and current device.</summary>
public sealed class GetProfileHandler(IUserRepository users, IDeviceRepository devices, TimeProvider timeProvider)
{
    public async Task<Result<UserProfile>> HandleAsync(UserId userId, DeviceId? currentDeviceId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        var now = timeProvider.GetUtcNow();
        DeviceSummary? current = null;
        if (currentDeviceId is { } deviceId &&
            await devices.GetByIdAsync(deviceId, cancellationToken) is { } device &&
            device.UserId == userId)
        {
            current = DeviceSummary.From(device, deviceId, now);
        }

        return new UserProfile(
            user.Id,
            user.PhoneNumber.Masked,
            user.KycTier,
            user.Role.ToClaimValue(),
            user.Status,
            user.CreatedAt,
            user.LastLoginAt,
            current);
    }
}

/// <summary>Lists the signed-in user's devices, trusted and revoked.</summary>
public sealed class ListDevicesHandler(IDeviceRepository devices, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DeviceSummary>> HandleAsync(UserId userId, DeviceId? currentDeviceId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var list = await devices.ListByUserAsync(userId, cancellationToken);
        return [.. list.Select(device => DeviceSummary.From(device, currentDeviceId, now))];
    }
}

/// <summary>
/// Removes a device from the user's trusted devices (for example a lost phone) and ends its sessions.
/// Its next login needs a new OTP.
/// </summary>
public sealed class RevokeDeviceHandler(
    IDeviceRepository devices,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result> HandleAsync(UserId userId, DeviceId deviceId, CancellationToken cancellationToken)
    {
        var device = await devices.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.UserId != userId)
        {
            return AccountErrors.DeviceNotFound;
        }

        var now = timeProvider.GetUtcNow();
        device.Revoke(now);
        foreach (var token in await refreshTokens.ListUnusedByDeviceAsync(deviceId, cancellationToken))
        {
            token.Revoke(now);
        }

        return await unitOfWork.TrySaveChangesAsync(AuthErrors.ConcurrentRequest, cancellationToken);
    }
}
