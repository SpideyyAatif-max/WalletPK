using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Application.Abstractions;

public interface IDeviceRepository
{
    Task<Device?> GetByIdAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>The user's device with this installation ID, trusted or revoked; null if never seen.</summary>
    Task<Device?> GetByInstallationAsync(UserId userId, string installationId, CancellationToken cancellationToken);

    /// <summary>True when the user has any device record other than this installation (so this is not their first phone).</summary>
    Task<bool> HasOtherDevicesAsync(UserId userId, string installationId, CancellationToken cancellationToken);

    /// <summary>All of the user's devices, most recently used first.</summary>
    Task<IReadOnlyList<Device>> ListByUserAsync(UserId userId, CancellationToken cancellationToken);

    void Add(Device device);
}
