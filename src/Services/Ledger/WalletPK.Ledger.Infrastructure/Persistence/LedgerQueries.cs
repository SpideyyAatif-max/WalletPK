using Microsoft.EntityFrameworkCore;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Infrastructure.Persistence;

/// <summary>
/// Read-side queries (no change tracking). History pages use keyset paging on
/// ix_journal_entries_account_history (account_id, id), so page 1000 is as fast as page 1.
/// </summary>
internal sealed class LedgerQueries(LedgerDbContext db) : ILedgerQueries
{
    public async Task<IReadOnlyList<HistoryLine>> GetHistoryAsync(AccountId accountId, int limit, long? beforeEntryId, CancellationToken cancellationToken)
    {
        var entries = db.JournalEntries.AsNoTracking().Where(entry => entry.AccountId == accountId);
        if (beforeEntryId is { } before)
        {
            entries = entries.Where(entry => entry.Id < before);
        }

        var page = await entries
            .OrderByDescending(entry => entry.Id)
            .Take(limit)
            .Join(
                db.JournalTransactions.AsNoTracking(),
                entry => entry.TransactionId,
                journal => journal.Id,
                (entry, journal) => new
                {
                    entry.Id,
                    entry.TransactionId,
                    journal.Type,
                    journal.Description,
                    entry.Direction,
                    entry.Amount,
                    entry.BalanceAfter,
                    entry.PostedAt,
                })
            .OrderByDescending(row => row.Id)
            .ToListAsync(cancellationToken);

        if (page.Count == 0)
        {
            return [];
        }

        // The other wallet in each journal (the person paid or paid by), if there is one.
        var transactionIds = page.Select(row => row.TransactionId).Distinct().ToList();
        var counterparties = await db.JournalEntries.AsNoTracking()
            .Where(entry => transactionIds.Contains(entry.TransactionId) && entry.AccountId != accountId)
            .Join(
                db.Accounts.AsNoTracking().Where(account => account.Type != AccountType.System && account.Alias != null),
                entry => entry.AccountId,
                account => account.Id,
                (entry, account) => new { entry.TransactionId, account.Alias })
            .ToListAsync(cancellationToken);
        var counterpartyByTransaction = counterparties
            .GroupBy(row => row.TransactionId)
            .ToDictionary(group => group.Key, group => group.First().Alias);

        return
        [
            .. page.Select(row => new HistoryLine(
                row.Id,
                row.TransactionId,
                row.Type,
                row.Description,
                row.Direction,
                Money.FromPaisa(row.Amount),
                Money.FromPaisa(row.BalanceAfter),
                row.PostedAt,
                counterpartyByTransaction.GetValueOrDefault(row.TransactionId))),
        ];
    }
}
