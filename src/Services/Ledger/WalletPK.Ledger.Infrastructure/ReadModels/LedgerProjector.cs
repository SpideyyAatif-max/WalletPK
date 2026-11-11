using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>
/// Applies ledger.events to the read models (M4.4), one event per database transaction:
/// <list type="bullet">
/// <item><c>ledger.wallet_opened</c> → a row in read.wallet_balances (balance 0).</item>
/// <item><c>ledger.wallet_entry_posted</c> → a row in read.transaction_history and the wallet's new
/// balance in read.wallet_balances.</item>
/// <item><c>ledger.journal_posted</c> → the journal counted once in read.metrics_per_minute.</item>
/// </list>
/// Idempotent without an inbox, so a topic can be replayed from offset 0 (M4.6): history rows are keyed
/// by (wallet, sequence) and inserted only once; a balance only moves forward (higher sequence wins);
/// metrics count a journal only the first time it is seen (read.projected_journals).
/// <para>
/// Each transaction first reads the projection's generation with FOR SHARE. A rebuild bumps the
/// generation (and empties the tables) in one transaction, so a consumer of the old generation that is
/// still running can never write into the fresh tables: it sees the new number and skips.
/// </para>
/// </summary>
internal sealed partial class LedgerProjector(
    LedgerReadModelDatabase database,
    JsonEventSerializer serializer,
    ReadModelOptions options,
    ReadModelMetrics metrics,
    TimeProvider timeProvider,
    ILogger<LedgerProjector> logger) : IEnvelopeProcessor
{
    public async Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!serializer.TryUnwrap(envelope, out var integrationEvent) || integrationEvent is not (WalletOpened or WalletEntryPosted or JournalPosted))
        {
            return; // Not something the read models show.
        }

        var generation = options.GenerationOf(consumer)
            ?? throw new InvalidOperationException($"Consumer group '{consumer}' is not a read-model generation.");

        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT generation FROM read.projection_state WHERE name = @Name FOR SHARE",
            new { Name = LedgerReadModelDatabase.ProjectionName },
            transaction,
            cancellationToken: cancellationToken));
        if (current != generation)
        {
            // A rebuild started a newer generation; its consumer replays this event itself.
            metrics.Projected(envelope.EventType, "stale_generation", TimeSpan.Zero);
            return;
        }

        switch (integrationEvent)
        {
            case WalletOpened opened:
                await ApplyAsync(connection, transaction, opened, cancellationToken);
                break;
            case WalletEntryPosted entry:
                await ApplyAsync(connection, transaction, entry, cancellationToken);
                break;
            case JournalPosted journal:
                await ApplyAsync(connection, transaction, journal, cancellationToken);
                break;
        }

        await transaction.CommitAsync(cancellationToken);
        metrics.Projected(envelope.EventType, "applied", timeProvider.GetUtcNow() - envelope.OccurredAt);
    }

    private static Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, WalletOpened opened, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO read.wallet_balances AS b
                (wallet_id, owner_id, wallet_number, wallet_type, balance_paisa, last_sequence, opened_at, updated_at)
            VALUES (@WalletId, @OwnerId, @WalletNumber, @WalletType, 0, 0, @OpenedAt, @OpenedAt)
            ON CONFLICT (wallet_id) DO UPDATE SET
                opened_at = EXCLUDED.opened_at,
                owner_id = COALESCE(b.owner_id, EXCLUDED.owner_id),
                wallet_number = COALESCE(b.wallet_number, EXCLUDED.wallet_number)
            """,
            new
            {
                opened.WalletId,
                OwnerId = (Guid?)opened.OwnerId,
                opened.WalletNumber,
                opened.WalletType,
                OpenedAt = opened.OpenedAt.UtcDateTime,
            },
            transaction,
            cancellationToken: cancellationToken));

    private async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, WalletEntryPosted entry, CancellationToken cancellationToken)
    {
        var lastSequence = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT last_sequence FROM read.wallet_balances WHERE wallet_id = @WalletId",
            new { entry.WalletId },
            transaction,
            cancellationToken: cancellationToken)) ?? 0;

        if (entry.Sequence > lastSequence + 1)
        {
            // Kafka keeps one wallet's events in order, so the missing ones are not coming (for example
            // entries posted before the event pipeline existed). Applied anyway; the status shows the gap.
            metrics.Gap();
            LogGap(logger, entry.WalletId, lastSequence, entry.Sequence);
        }

        var parameters = new
        {
            entry.WalletId,
            entry.Sequence,
            entry.TransactionId,
            entry.JournalType,
            entry.Description,
            entry.Direction,
            entry.AmountPaisa,
            entry.BalanceAfterPaisa,
            entry.CounterpartyWalletNumber,
            PostedAt = entry.PostedAt.UtcDateTime,
            entry.OwnerId,
            entry.WalletNumber,
            entry.WalletType,
        };

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO read.transaction_history
                (wallet_id, sequence, transaction_id, journal_type, description, direction,
                 amount_paisa, balance_after_paisa, counterparty_wallet_number, posted_at)
            VALUES (@WalletId, @Sequence, @TransactionId, @JournalType, @Description, @Direction,
                    @AmountPaisa, @BalanceAfterPaisa, @CounterpartyWalletNumber, @PostedAt)
            ON CONFLICT (wallet_id, sequence) DO NOTHING;

            INSERT INTO read.wallet_balances AS b
                (wallet_id, owner_id, wallet_number, wallet_type, balance_paisa, last_sequence, updated_at)
            VALUES (@WalletId, @OwnerId, @WalletNumber, @WalletType, @BalanceAfterPaisa, @Sequence, @PostedAt)
            ON CONFLICT (wallet_id) DO UPDATE SET
                balance_paisa = EXCLUDED.balance_paisa,
                last_sequence = EXCLUDED.last_sequence,
                updated_at = EXCLUDED.updated_at,
                owner_id = COALESCE(b.owner_id, EXCLUDED.owner_id),
                wallet_number = COALESCE(b.wallet_number, EXCLUDED.wallet_number)
            WHERE b.last_sequence < EXCLUDED.last_sequence;
            """,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JournalPosted journal, CancellationToken cancellationToken)
    {
        var postedAt = journal.PostedAt.UtcDateTime;
        var minute = new DateTime(postedAt.Ticks - (postedAt.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            WITH first_time AS (
                INSERT INTO read.projected_journals (transaction_id, journal_type, amount_paisa, posted_at)
                VALUES (@TransactionId, @JournalType, @AmountPaisa, @PostedAt)
                ON CONFLICT (transaction_id) DO NOTHING
                RETURNING transaction_id)
            INSERT INTO read.metrics_per_minute AS m (minute, journal_type, journal_count, volume_paisa)
            SELECT @Minute, @JournalType, 1, @AmountPaisa FROM first_time
            ON CONFLICT (minute, journal_type) DO UPDATE SET
                journal_count = m.journal_count + 1,
                volume_paisa = m.volume_paisa + EXCLUDED.volume_paisa
            """,
            new { journal.TransactionId, journal.JournalType, journal.AmountPaisa, PostedAt = postedAt, Minute = minute },
            transaction,
            cancellationToken: cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wallet {WalletId} history jumps from entry {LastSequence} to {Sequence}; the entries in between never reached the read model")]
    private static partial void LogGap(ILogger logger, Guid walletId, long lastSequence, long sequence);
}
