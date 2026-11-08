using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;

namespace WalletPK.Identity.Infrastructure.Persistence;

internal sealed class DeviceRepository(IdentityDbContext dbContext) : IDeviceRepository
{
    public Task<Device?> GetByIdAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        dbContext.Devices.FirstOrDefaultAsync(device => device.Id == deviceId, cancellationToken);

    public Task<Device?> GetByInstallationAsync(UserId userId, string installationId, CancellationToken cancellationToken) =>
        dbContext.Devices.FirstOrDefaultAsync(
            device => device.UserId == userId && device.InstallationId == installationId,
            cancellationToken);

    public Task<bool> HasOtherDevicesAsync(UserId userId, string installationId, CancellationToken cancellationToken) =>
        dbContext.Devices.AnyAsync(
            device => device.UserId == userId && device.InstallationId != installationId,
            cancellationToken);

    public async Task<IReadOnlyList<Device>> ListByUserAsync(UserId userId, CancellationToken cancellationToken) =>
        await dbContext.Devices
            .Where(device => device.UserId == userId)
            .OrderByDescending(device => device.LastSeenAt)
            .ToListAsync(cancellationToken);

    public void Add(Device device) => dbContext.Devices.Add(device);
}
