using System.Text;
using Dapper;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.ReadModels;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>
/// The query side (M4.5): hand-written SQL through Dapper on the read models, each query a single
/// index range scan. History pages use keyset paging on the primary key (wallet_id, sequence), so the
/// thousandth page costs the same as the first; a type filter uses (wallet_id, journal_type, sequence).
/// </summary>
internal sealed class ReadModelQueries(LedgerReadModelDatabase database) : IReadModelQueries
{
    public async Task<WalletBalanceRow?> GetWalletAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken)
    {
        await using var connection = await database.Reader.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<WalletRecord>(new CommandDefinition(
            """
            SELECT wallet_id AS WalletId, wallet_number AS WalletNumber, wallet_type AS WalletType,
                   balance_paisa AS BalancePaisa, last_sequence AS LastSequence, updated_at AS UpdatedAt
            FROM read.wallet_balances
            WHERE owner_id = @OwnerId AND wallet_type = @WalletType
            ORDER BY opened_at NULLS LAST
            LIMIT 1
            """,
            new { OwnerId = ownerId.Value, WalletType = type.ToString() },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new WalletBalanceRow(
                new AccountId(row.WalletId),
                row.WalletNumber,
                Enum.Parse<AccountType>(row.WalletType),
                Money.FromPaisa(row.BalancePaisa),
                row.LastSequence,
                AsUtc(row.UpdatedAt));
    }

    public async Task<IReadOnlyList<HistoryRow>> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Only the filters in use go into the SQL, so the planner always sees the simplest query.
        var sql = new StringBuilder(
            """
            SELECT sequence AS Sequence, transaction_id AS TransactionId, journal_type AS JournalType,
                   description AS Description, direction AS Direction, amount_paisa AS AmountPaisa,
                   balance_after_paisa AS BalanceAfterPaisa, counterparty_wallet_number AS CounterpartyWalletNumber,
                   posted_at AS PostedAt
            FROM read.transaction_history
            WHERE wallet_id = @WalletId
            """);
        var parameters = new DynamicParameters(new { WalletId = query.WalletId.Value, query.Limit });
        if (query.BeforeSequence is { } before)
        {
            sql.Append(" AND sequence < @Before");
            parameters.Add("Before", before);
        }

        if (query.Type is { } type)
        {
            sql.Append(" AND journal_type = @Type");
            parameters.Add("Type", type.ToString());
        }

        if (query.Direction is { } direction)
        {
            sql.Append(" AND direction = @Direction");
            parameters.Add("Direction", direction.ToString());
        }

        if (query.From is { } from)
        {
            sql.Append(" AND posted_at >= @From");
            parameters.Add("From", from.UtcDateTime);
        }

        if (query.To is { } to)
        {
            sql.Append(" AND posted_at < @To");
            parameters.Add("To", to.UtcDateTime);
        }

        sql.Append(" ORDER BY sequence DESC LIMIT @Limit");

        await using var connection = await database.Reader.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<HistoryRecord>(new CommandDefinition(sql.ToString(), parameters, cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new HistoryRow(
                row.Sequence,
                new TransactionId(row.TransactionId),
                Enum.Parse<JournalType>(row.JournalType),
                row.Description,
                Enum.Parse<EntryDirection>(row.Direction),
                Money.FromPaisa(row.AmountPaisa),
                Money.FromPaisa(row.BalanceAfterPaisa),
                row.CounterpartyWalletNumber,
                AsUtc(row.PostedAt))),
        ];
    }

    public async Task<IReadOnlyList<MinuteMetric>> GetMetricsAsync(DateTimeOffset from, DateTimeOffset to, JournalType? type, CancellationToken cancellationToken)
    {
        await using var connection = await database.Reader.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MetricRecord>(new CommandDefinition(
            """
            SELECT minute AS Minute, journal_type AS JournalType, journal_count AS JournalCount, volume_paisa AS VolumePaisa
            FROM read.metrics_per_minute
            WHERE minute >= @From AND minute < @To AND (@Type::text IS NULL OR journal_type = @Type)
            ORDER BY minute, journal_type
            """,
            new { From = from.UtcDateTime, To = to.UtcDateTime, Type = type?.ToString() },
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => new MinuteMetric(AsUtc(row.Minute), row.JournalType, row.JournalCount, Money.FromPaisa(row.VolumePaisa)))];
    }

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Dapper fills these by column alias.
    private sealed class WalletRecord
    {
        public Guid WalletId { get; set; }

        public string? WalletNumber { get; set; }

        public string WalletType { get; set; } = "";

        public long BalancePaisa { get; set; }

        public long LastSequence { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    private sealed class HistoryRecord
    {
        public long Sequence { get; set; }

        public Guid TransactionId { get; set; }

        public string JournalType { get; set; } = "";

        public string Description { get; set; } = "";

        public string Direction { get; set; } = "";

        public long AmountPaisa { get; set; }

        public long BalanceAfterPaisa { get; set; }

        public string? CounterpartyWalletNumber { get; set; }

        public DateTime PostedAt { get; set; }
    }

    private sealed class MetricRecord
    {
        public DateTime Minute { get; set; }

        public string JournalType { get; set; } = "";

        public long JournalCount { get; set; }

        public long VolumePaisa { get; set; }
    }
}
