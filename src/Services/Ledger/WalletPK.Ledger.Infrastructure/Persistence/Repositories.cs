using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Infrastructure.Persistence;

internal sealed class AccountRepository(LedgerDbContext dbContext) : IAccountRepository
{
    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken) =>
        dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

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
