using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Kyc;

namespace WalletPK.Identity.Application.Abstractions;

public interface IKycProfileRepository
{
    Task<KycProfile?> GetByUserIdAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>True when any account already uses the CNIC with this blind-index hash.</summary>
    Task<bool> ExistsByCnicLookupAsync(string cnicLookupHash, CancellationToken cancellationToken);

    void Add(KycProfile profile);
}
