using Microsoft.EntityFrameworkCore;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;

namespace WalletPK.Identity.Infrastructure.Persistence;

internal sealed class RefreshTokenRepository(IdentityDbContext dbContext) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListByFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens
            .Where(token => token.FamilyId == familyId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListUnusedByDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
        await dbContext.RefreshTokens
            .Where(token => token.DeviceId == deviceId && token.ConsumedAt == null && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

    public void Add(RefreshToken refreshToken) => dbContext.RefreshTokens.Add(refreshToken);
}
