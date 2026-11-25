using System.Globalization;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Reports;

/// <summary>One line of a monthly statement.</summary>
/// <param name="PostedAt">When (UTC; printed in Pakistan time).</param>
/// <param name="Title">What it was, as the app shows it.</param>
/// <param name="Type">The kind of journal.</param>
/// <param name="TransactionId">The journal's ID, for disputes.</param>
/// <param name="Credit">Money in, or null.</param>
/// <param name="Debit">Money out, or null.</param>
/// <param name="BalanceAfter">The balance right after it.</param>
public sealed record StatementEntry(DateTimeOffset PostedAt, string Title, JournalType Type, Guid TransactionId, Money? Credit, Money? Debit, Money BalanceAfter);

/// <summary>A customer's statement for one calendar month, Pakistan time (M12.5).</summary>
/// <param name="WalletNumber">The wallet's mobile number.</param>
/// <param name="Month">The month.</param>
/// <param name="From">Its first instant.</param>
/// <param name="To">The instant after its last (or now, for the month in progress).</param>
/// <param name="Opening">The balance when it began.</param>
/// <param name="Closing">The balance when it ended.</param>
/// <param name="MoneyIn">Everything credited.</param>
/// <param name="MoneyOut">Everything debited.</param>
/// <param name="Entries">Every entry, oldest first.</param>
/// <param name="GeneratedAt">When the statement was made.</param>
/// <param name="Complete">False for the month in progress.</param>
public sealed record MonthlyStatement(
    string? WalletNumber,
    StatementMonth Month,
    DateTimeOffset From,
    DateTimeOffset To,
    Money Opening,
    Money Closing,
    Money MoneyIn,
    Money MoneyOut,
    IReadOnlyList<StatementEntry> Entries,
    DateTimeOffset GeneratedAt,
    bool Complete);

/// <summary>A calendar month, e.g. "2026-11".</summary>
public readonly record struct StatementMonth(int Year, int Month)
{
    private static readonly TimeSpan PakistanOffset = TimeSpan.FromHours(5);

    public static StatementMonth? Parse(string? text) =>
        DateOnly.TryParseExact($"{text}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new StatementMonth(date.Year, date.Month)
            : null;

    public DateTimeOffset Start => new(Year, Month, 1, 0, 0, 0, PakistanOffset);

    public DateTimeOffset End => Start.AddMonths(1);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");

    public string Name => Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Turns a statement into a PDF.</summary>
public interface IStatementRenderer
{
    byte[] Render(MonthlyStatement statement);
}

/// <summary>Journals of one type on one day.</summary>
/// <param name="Type">e.g. "P2PTransfer".</param>
/// <param name="Count">How many.</param>
/// <param name="VolumePaisa">Their total.</param>
public sealed record JournalTotal(string Type, long Count, long VolumePaisa);

/// <summary>A business day's figures from the ledger (M12.7).</summary>
/// <param name="Date">The day (Pakistan time).</param>
/// <param name="Journals">By type.</param>
/// <param name="JournalCount">All journals.</param>
/// <param name="VolumePaisa">All money moved.</param>
/// <param name="FeeIncomePaisa">Fees earned (FEE_INCOME credits less debits).</param>
/// <param name="TaxCollectedPaisa">Tax collected on fees (TAX_PAYABLE credits less debits).</param>
/// <param name="CommissionPaisa">Agent commissions accrued (COMMISSION_EXPENSE debits less credits).</param>
/// <param name="WalletsOpened">Wallets opened that day.</param>
/// <param name="ActiveWallets">Wallets with at least one entry that day.</param>
public sealed record DayTotals(
    DateOnly Date,
    IReadOnlyList<JournalTotal> Journals,
    long JournalCount,
    long VolumePaisa,
    long FeeIncomePaisa,
    long TaxCollectedPaisa,
    long CommissionPaisa,
    long WalletsOpened,
    long ActiveWallets);

/// <summary>What the ledger holds now, by kind of account.</summary>
/// <param name="Type">CustomerWallet, MerchantWallet, AgentWallet, or a system account code.</param>
/// <param name="Accounts">How many accounts.</param>
/// <param name="BalancePaisa">Their total balance.</param>
public sealed record BalanceTotal(string Type, long Accounts, long BalancePaisa);

/// <summary>Business figures straight from the ledger tables (the source of truth, not the read models).</summary>
public interface IBusinessReportQueries
{
    /// <summary>One row per day in [<paramref name="from"/>, <paramref name="to"/>], Pakistan time, days without journals included.</summary>
    Task<IReadOnlyList<DayTotals>> DaysAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<BalanceTotal>> BalancesAsync(CancellationToken cancellationToken);
}
