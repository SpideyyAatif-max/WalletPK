using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Abstractions;

public interface IUserRepository
{
    Task<bool> ExistsByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken);

    Task<User?> GetByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken);

    Task<User?> GetByIdAsync(UserId userId, CancellationToken cancellationToken);

    void Add(User user);
}
