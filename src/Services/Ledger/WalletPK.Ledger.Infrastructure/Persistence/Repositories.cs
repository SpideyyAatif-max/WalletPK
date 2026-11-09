using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Infrastructure.Persistence;

internal sealed class AccountRepository(LedgerDbContext dbContext) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken) =>
        dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

    public Task<Account?> GetWalletByOwnerAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken) =>
        dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.OwnerId == ownerId && account.Type == type, cancellationToken);

    public Task<Account?> GetWalletByAliasAsync(string alias, CancellationToken cancellationToken) =>
        dbContext.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Alias == alias, cancellationToken);

    public async Task<IReadOnlyList<Account>> GetManyAsync(IReadOnlyCollection<AccountId> ids, CancellationToken cancellationToken) =>
        await dbContext.Accounts.Where(account => ids.Contains(account.Id)).ToListAsync(cancellationToken);

    public void Add(Account account) => dbContext.Accounts.Add(account);
}

internal sealed class JournalRepository(LedgerDbContext dbContext) : IJournalRepository
{
    public Task<JournalTransaction?> GetAsync(TransactionId id, CancellationToken cancellationToken) =>
        dbContext.JournalTransactions
            .Include(journal => journal.Entries)
            .FirstOrDefaultAsync(journal => journal.Id == id, cancellationToken);

    public void Add(JournalTransaction journal) => dbContext.JournalTransactions.Add(journal);
}

internal sealed class HoldRepository(LedgerDbContext dbContext) : IHoldRepository
{
    public Task<Hold?> GetAsync(HoldId id, CancellationToken cancellationToken) =>
        dbContext.Holds.FirstOrDefaultAsync(hold => hold.Id == id, cancellationToken);

    public Task<Hold?> PeekAsync(HoldId id, CancellationToken cancellationToken) =>
        dbContext.Holds.AsNoTracking().FirstOrDefaultAsync(hold => hold.Id == id, cancellationToken);

    public async Task<IReadOnlyList<HoldId>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.Holds
            .AsNoTracking()
            .Where(hold => hold.Status == HoldStatus.Active && hold.ExpiresAt <= now)
            .OrderBy(hold => hold.ExpiresAt)
            .Select(hold => hold.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public void Add(Hold hold) => dbContext.Holds.Add(hold);
}
