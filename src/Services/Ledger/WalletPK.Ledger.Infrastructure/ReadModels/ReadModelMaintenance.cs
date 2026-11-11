using Dapper;
using WalletPK.Ledger.Application.ReadModels;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>Status and rebuild of the read models (M4.6).</summary>
internal sealed class ReadModelMaintenance(LedgerReadModelDatabase database, TimeProvider timeProvider) : IReadModelMaintenance
{
    public async Task<ReadModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<ReadModelStatusRecord>(new CommandDefinition(
            """
            SELECT p.generation AS Generation,
                   p.rebuilt_at AS RebuiltAt,
                   (SELECT count(*) FROM read.wallet_balances) AS Wallets,
                   (SELECT count(*) FROM read.transaction_history) AS HistoryRows,
                   (SELECT count(*) FROM journal_entries e JOIN accounts a ON a.id = e.account_id WHERE a.type <> 'System') AS WalletEntries,
                   (SELECT count(*) FROM read.projected_journals) AS ProjectedJournals,
                   (SELECT count(*) FROM journal_transactions) AS Journals
            FROM read.projection_state p
            WHERE p.name = @Name
            """,
            new { Name = LedgerReadModelDatabase.ProjectionName },
            cancellationToken: cancellationToken));

        return new ReadModelStatus(
            row.Generation,
            row.RebuiltAt is { } rebuiltAt ? new DateTimeOffset(DateTime.SpecifyKind(rebuiltAt, DateTimeKind.Utc)) : null,
            row.Wallets,
            row.HistoryRows,
            row.WalletEntries,
            row.ProjectedJournals,
            row.Journals);
    }

    public async Task<long> RebuildAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The UPDATE waits for projector transactions holding the generation row (FOR SHARE) to finish;
        // after the commit, every consumer of the old generation sees the new number and stops writing.
        var generation = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            UPDATE read.projection_state SET generation = generation + 1, rebuilt_at = @Now
            WHERE name = @Name
            RETURNING generation;

            TRUNCATE read.transaction_history, read.wallet_balances, read.projected_journals, read.metrics_per_minute;
            """,
            new { Name = LedgerReadModelDatabase.ProjectionName, Now = timeProvider.GetUtcNow().UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return generation;
    }

    private sealed class ReadModelStatusRecord
    {
        public long Generation { get; set; }

        public DateTime? RebuiltAt { get; set; }

        public long Wallets { get; set; }

        public long HistoryRows { get; set; }

        public long WalletEntries { get; set; }

        public long ProjectedJournals { get; set; }

        public long Journals { get; set; }
    }
}
