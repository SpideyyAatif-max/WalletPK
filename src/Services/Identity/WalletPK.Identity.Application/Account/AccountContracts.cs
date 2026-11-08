using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Account;

/// <summary>The signed-in user's own profile (GET /api/identity/me).</summary>
/// <param name="UserId">The user's ID.</param>
/// <param name="PhoneNumber">Masked, for example "+92300*****67".</param>
/// <param name="KycTier">L0, L1 or L2.</param>
/// <param name="Role">"customer", "merchant", "agent" or a staff role.</param>
/// <param name="Status">Active, Locked or Frozen.</param>
/// <param name="RegisteredAt">When the account was created.</param>
/// <param name="LastLoginAt">The most recent successful login.</param>
/// <param name="CurrentDevice">The device this request came from.</param>
public sealed record UserProfile(
    UserId UserId,
    string PhoneNumber,
    KycTier KycTier,
    string Role,
    UserStatus Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastLoginAt,
    DeviceSummary? CurrentDevice);

/// <summary>One entry of the user's device list.</summary>
/// <param name="DeviceId">The device's ID.</param>
/// <param name="Name">Name the app reported, for example "Samsung Galaxy A54".</param>
/// <param name="Status">Trusted or Revoked.</param>
/// <param name="TrustedAt">When the device was verified with an OTP.</param>
/// <param name="LastSeenAt">Last login or refresh from this device.</param>
/// <param name="CoolingOffUntil">End of the cooling-off period, if one is still running.</param>
/// <param name="IsCurrent">True for the device making this request.</param>
public sealed record DeviceSummary(
    DeviceId DeviceId,
    string Name,
    DeviceStatus Status,
    DateTimeOffset TrustedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? CoolingOffUntil,
    bool IsCurrent)
{
    public static DeviceSummary From(Device device, DeviceId? currentDeviceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceSummary(
            device.Id,
            device.Name,
            device.Status,
            device.TrustedAt,
            device.LastSeenAt,
            device.ActiveCoolingOffUntil(now),
            device.Id == currentDeviceId);
    }
}
