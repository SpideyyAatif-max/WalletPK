using Microsoft.EntityFrameworkCore;
using WalletPK.Ledger.Infrastructure.Persistence;

namespace WalletPK.Ledger.Infrastructure.Audit;

/// <param name="Intact">True when every sealed row's hash and link check out and the sequence has no gaps.</param>
/// <param name="SealedCount">Rows in the chain.</param>
/// <param name="PendingCount">Rows written but not sealed yet.</param>
/// <param name="OldestPendingAt">When the oldest unsealed row was written, if any.</param>
/// <param name="HeadSequence">The last chain position.</param>
/// <param name="HeadHash">The hash at the head; compare with the value logged when it was sealed.</param>
/// <param name="FirstBrokenSequence">Where verification failed, if it did.</param>
/// <param name="Problem">What was wrong there.</param>
public sealed record AuditChainReport(
    bool Intact,
    long SealedCount,
    long PendingCount,
    DateTimeOffset? OldestPendingAt,
    long HeadSequence,
    string HeadHash,
    long? FirstBrokenSequence,
    string? Problem);

/// <summary>
/// Walks the whole audit chain in order and recomputes every hash (M3.10). Reads in pages, so memory
/// stays flat however long the chain is.
/// </summary>
public sealed class AuditChainVerifier(LedgerDbContext db)
{
    private const int PageSize = 1_000;

    public async Task<AuditChainReport> VerifyAsync(CancellationToken cancellationToken)
    {
        var records = db.Set<AuditRecord>().AsNoTracking();
        var pendingCount = await records.LongCountAsync(record => record.Hash == null, cancellationToken);
        var oldestPendingAt = await records
            .Where(record => record.Hash == null)
            .OrderBy(record => record.Id)
            .Select(record => (DateTimeOffset?)record.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        long expectedSequence = 1;
        var previousHash = AuditChain.GenesisHash;

        while (true)
        {
            var from = expectedSequence;
            var page = await records
                .Where(record => record.ChainSequence >= from)
                .OrderBy(record => record.ChainSequence)
                .Take(PageSize)
                .ToListAsync(cancellationToken);

            foreach (var record in page)
            {
                var problem =
                    record.ChainSequence != expectedSequence ? $"Chain position {expectedSequence} is missing (next row found is #{record.ChainSequence})."
                    : record.PreviousHash != previousHash ? $"Row #{expectedSequence} does not point at the hash of the row before it (a row was changed, removed or inserted)."
                    : record.Hash != AuditChain.ComputeHash(record, expectedSequence, previousHash) ? $"Row #{expectedSequence} (audit id {record.Id}) was modified after it was sealed."
                    : null;

                if (problem is not null)
                {
                    return new AuditChainReport(false, expectedSequence - 1, pendingCount, oldestPendingAt, expectedSequence - 1, previousHash, expectedSequence, problem);
                }

                previousHash = record.Hash!;
                expectedSequence++;
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        return new AuditChainReport(true, expectedSequence - 1, pendingCount, oldestPendingAt, expectedSequence - 1, previousHash, null, null);
    }
}

/// <summary>One audit row as the audit viewer shows it (M12.6).</summary>
/// <param name="Id">Insert order.</param>
/// <param name="ChainSequence">Position in the chain, once sealed.</param>
/// <param name="OccurredAt">When the change happened.</param>
/// <param name="EventType">e.g. "journal.posted".</param>
/// <param name="SubjectType">"journal", "hold" or "account".</param>
/// <param name="SubjectId">Which one.</param>
/// <param name="Actor">"user:{id}" or "system".</param>
/// <param name="Payload">What happened, as JSON.</param>
/// <param name="PreviousHash">The hash of the row before it in the chain.</param>
/// <param name="Hash">Its own hash, once sealed.</param>
/// <param name="SealedAt">When it was chained.</param>
public sealed record AuditRow(
    long Id, long? ChainSequence, DateTimeOffset OccurredAt, string EventType, string SubjectType, string SubjectId,
    string Actor, string Payload, string? PreviousHash, string? Hash, DateTimeOffset? SealedAt);

/// <summary>The audit log, newest first, for the audit viewer.</summary>
public sealed class AuditTrail(LedgerDbContext db)
{
    public async Task<IReadOnlyList<AuditRow>> RecentAsync(long? before, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 500);
        return await db.Set<AuditRecord>().AsNoTracking()
            .Where(record => before == null || record.Id < before)
            .OrderByDescending(record => record.Id)
            .Take(take)
            .Select(record => new AuditRow(
                record.Id, record.ChainSequence, record.OccurredAt, record.EventType, record.SubjectType, record.SubjectId,
                record.Actor, record.Payload, record.PreviousHash, record.Hash, record.SealedAt))
            .ToListAsync(cancellationToken);
    }
}
