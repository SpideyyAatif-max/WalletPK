using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<bool> ExistsByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(user => user.PhoneNumber == phoneNumber, cancellationToken);

    public Task<User?> GetByPhoneAsync(PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.PhoneNumber == phoneNumber, cancellationToken);

    public Task<User?> GetByIdAsync(UserId userId, CancellationToken cancellationToken) =>
        dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public void Add(User user) => dbContext.Users.Add(user);
}
