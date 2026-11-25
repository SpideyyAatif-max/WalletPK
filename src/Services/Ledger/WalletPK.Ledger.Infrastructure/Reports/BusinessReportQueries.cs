using Dapper;
using WalletPK.Ledger.Application.Reports;
using WalletPK.Ledger.Infrastructure.ReadModels;

namespace WalletPK.Ledger.Infrastructure.Reports;

/// <summary>
/// Business figures (M12.4, M12.7) read from the ledger tables themselves on the primary: these are the
/// books, so a report never shows a read model that is a few events behind. Days are Pakistan days.
/// </summary>
internal sealed class BusinessReportQueries(LedgerReadModelDatabase database) : IBusinessReportQueries
{
    /// <summary>A timestamp's Pakistan business date in SQL (UTC+05:00, no daylight saving).</summary>
    private const string Day = "((posted_at AT TIME ZONE 'UTC') + interval '5 hours')::date";

    public async Task<IReadOnlyList<DayTotals>> DaysAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5)).UtcDateTime;
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5)).UtcDateTime;
        var range = new { From = start, To = end };

        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        var journals = (await connection.QueryAsync<JournalRow>(new CommandDefinition(
            $"""
            SELECT {Day} AS Day, type AS Type, count(*) AS Count, sum(amount)::bigint AS Volume
            FROM journal_transactions
            WHERE posted_at >= @From AND posted_at < @To
            GROUP BY 1, 2
            """,
            range, cancellationToken: cancellationToken))).ToList();

        // Credits less debits on fee income and tax; debits less credits on commission expense.
        var system = (await connection.QueryAsync<SystemRow>(new CommandDefinition(
            $"""
            SELECT {Day.Replace("posted_at", "e.posted_at", StringComparison.Ordinal)} AS Day, a.system_code AS Code,
                   sum(CASE WHEN e.direction = 'Credit' THEN e.amount ELSE -e.amount END)::bigint AS Net
            FROM journal_entries e
            JOIN accounts a ON a.id = e.account_id
            WHERE a.system_code IN ('FEE_INCOME', 'TAX_PAYABLE', 'COMMISSION_EXPENSE')
              AND e.posted_at >= @From AND e.posted_at < @To
            GROUP BY 1, 2
            """,
            range, cancellationToken: cancellationToken))).ToList();

        var opened = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            $"""
            SELECT {Day.Replace("posted_at", "created_at", StringComparison.Ordinal)} AS Day, count(*) AS Count
            FROM accounts
            WHERE type <> 'System' AND created_at >= @From AND created_at < @To
            GROUP BY 1
            """,
            range, cancellationToken: cancellationToken))).ToList();

        var active = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            $"""
            SELECT {Day.Replace("posted_at", "e.posted_at", StringComparison.Ordinal)} AS Day, count(DISTINCT e.account_id) AS Count
            FROM journal_entries e
            JOIN accounts a ON a.id = e.account_id
            WHERE a.type <> 'System' AND e.posted_at >= @From AND e.posted_at < @To
            GROUP BY 1
            """,
            range, cancellationToken: cancellationToken))).ToList();

        var days = new List<DayTotals>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var day = date;
            var byType = journals.Where(row => row.Day == day).OrderBy(row => row.Type, StringComparer.Ordinal)
                .Select(row => new JournalTotal(row.Type, row.Count, row.Volume)).ToList();
            long Net(string code) => system.Where(row => row.Day == day && row.Code == code).Sum(row => row.Net);
            days.Add(new DayTotals(
                date,
                byType,
                byType.Sum(row => row.Count),
                byType.Sum(row => row.VolumePaisa),
                Net("FEE_INCOME"),
                Net("TAX_PAYABLE"),
                -Net("COMMISSION_EXPENSE"),
                opened.Where(row => row.Day == day).Sum(row => row.Count),
                active.Where(row => row.Day == day).Sum(row => row.Count)));
        }

        return days;
    }

    public async Task<IReadOnlyList<BalanceTotal>> BalancesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<BalanceRow>(new CommandDefinition(
            """
            SELECT CASE WHEN type = 'System' THEN system_code ELSE type END AS Type, count(*) AS Accounts, sum(balance)::bigint AS Balance
            FROM accounts
            GROUP BY 1
            ORDER BY 1
            """,
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new BalanceTotal(row.Type, row.Accounts, row.Balance))];
    }

    private sealed record JournalRow(DateOnly Day, string Type, long Count, long Volume);

    private sealed record SystemRow(DateOnly Day, string Code, long Net);

    private sealed record CountRow(DateOnly Day, long Count);

    private sealed record BalanceRow(string Type, long Accounts, long Balance);
}
