using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.Infrastructure.Persistence;

internal sealed class KycProfileRepository(IdentityDbContext dbContext) : IKycProfileRepository
{
    public Task<KycProfile?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken) =>
        dbContext.KycProfiles.FirstOrDefaultAsync(profile => profile.Id == userId, cancellationToken);

    public Task<bool> ExistsByCnicLookupAsync(string cnicLookupHash, CancellationToken cancellationToken) =>
        dbContext.KycProfiles.AnyAsync(profile => profile.CnicLookupHash == cnicLookupHash, cancellationToken);

    public void Add(KycProfile profile) => dbContext.KycProfiles.Add(profile);
}
