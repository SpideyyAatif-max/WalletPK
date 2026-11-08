using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;

namespace WalletPK.Identity.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> ListByFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    /// <summary>Tokens of the device that are neither consumed nor revoked.</summary>
    Task<IReadOnlyList<RefreshToken>> ListUnusedByDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken);

    void Add(RefreshToken refreshToken);
}
