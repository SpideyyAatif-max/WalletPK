using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalletPK.Ledger.Infrastructure.Persistence;

namespace WalletPK.Ledger.Infrastructure.Audit;

/// <summary>
/// Links new audit rows into the hash chain, oldest first, in batches.
/// <para>
/// Why not hash at insert time? Every payment would then have to wait for the previous payment's hash,
/// putting all wallets in one queue. Instead payments only insert their row (no shared lock), and this
/// sealer chains them a moment later. Exactly one sealer runs at a time across all instances (a
/// transaction-scoped advisory lock), and the unique chain_sequence index makes a fork impossible anyway.
/// </para>
/// </summary>
public sealed partial class AuditSealer(LedgerDbContext db, TimeProvider timeProvider, ILogger<AuditSealer> logger)
{
    /// <summary>Advisory lock key: "WPKAUDIT" as 64 bits.</summary>
    internal const long AdvisoryLockKey = 0x57504B4155444954;

    /// <summary>Seals up to <paramref name="batchSize"/> rows; returns how many. 0 when another instance is sealing.</summary>
    public async Task<int> SealPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var acquired = await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!acquired)
        {
            return 0;
        }

        var pending = await db.Set<AuditRecord>()
            .Where(record => record.Hash == null)
            .OrderBy(record => record.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var head = await db.Set<AuditRecord>()
            .AsNoTracking()
            .Where(record => record.ChainSequence != null)
            .OrderByDescending(record => record.ChainSequence)
            .Select(record => new { record.ChainSequence, record.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = head?.ChainSequence ?? 0;
        var previousHash = head?.Hash ?? AuditChain.GenesisHash;
        var now = timeProvider.GetUtcNow();
        foreach (var record in pending)
        {
            record.Seal(++sequence, previousHash, now);
            previousHash = record.Hash!;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The head hash in the logs (Seq) is the external anchor: rewriting the whole chain in the
        // database would not match what was logged.
        LogSealed(logger, pending.Count, sequence, previousHash);
        return pending.Count;
    }

    /// <summary>Seals until nothing is pending (or another instance holds the lock).</summary>
    public async Task<int> SealAllAsync(int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        int sealedNow;
        do
        {
            sealedNow = await SealPendingAsync(batchSize, cancellationToken);
            total += sealedNow;
            db.ChangeTracker.Clear();
        }
        while (sealedNow == batchSize);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain: sealed {Count} record(s); head is #{HeadSequence} {HeadHash}")]
    private static partial void LogSealed(ILogger logger, int count, long headSequence, string headHash);
}
