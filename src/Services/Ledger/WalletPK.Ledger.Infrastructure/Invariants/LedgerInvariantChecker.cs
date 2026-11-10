using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Persistence;

namespace WalletPK.Ledger.Infrastructure.Invariants;

/// <summary>
/// Proves the books are sound (M3.11). Every check runs inside one REPEATABLE READ, READ ONLY
/// transaction, so all of them see the same snapshot even while payments keep flowing: a payment
/// half-way through can never show up as a false alarm. The database triggers already stop most of
/// these from ever happening; this is the independent second opinion that would catch a trigger that
/// was dropped, a hand-run SQL fix, or a bug in the cached balances.
/// </summary>
public sealed class LedgerInvariantChecker(
    LedgerDbContext db,
    AuditChainVerifier auditVerifier,
    AuditOptions auditOptions,
    TimeProvider timeProvider) : ILedgerInvariantChecker
{
    public async Task<InvariantReport> CheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var checkedAt = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await QueryAsync(transaction, "SET TRANSACTION READ ONLY", cancellationToken);

        List<InvariantCheck> checks =
        [
            await DebitsEqualCreditsAsync(transaction, cancellationToken),
            await CountCheckAsync(
                transaction,
                "journals_balanced",
                "Every journal has at least two entries, its debits equal its credits, and its amount equals its debits.",
                """
                SELECT COUNT(*)
                  FROM journal_transactions j
                  LEFT JOIN (SELECT transaction_id,
                                    COUNT(*) AS lines,
                                    SUM(amount) FILTER (WHERE direction = 'Debit') AS debits,
                                    SUM(amount) FILTER (WHERE direction = 'Credit') AS credits
                               FROM journal_entries
                              GROUP BY transaction_id) e ON e.transaction_id = j.id
                 WHERE e.lines IS NULL OR e.lines < 2
                    OR e.debits IS DISTINCT FROM e.credits
                    OR j.amount IS DISTINCT FROM e.debits
                """,
                "unbalanced journal(s)",
                cancellationToken),
            await CountCheckAsync(
                transaction,
                "balances_match_entries",
                "Every account's stored balance equals the sum of its journal entries.",
                """
                SELECT COUNT(*)
                  FROM accounts a
                  LEFT JOIN (SELECT account_id,
                                    SUM(CASE WHEN direction = 'Credit' THEN amount ELSE -amount END) AS net_credit
                               FROM journal_entries
                              GROUP BY account_id) e ON e.account_id = a.id
                 WHERE a.balance <> CASE WHEN a.normal_balance = 'Credit' THEN COALESCE(e.net_credit, 0)
                                         ELSE -COALESCE(e.net_credit, 0) END
                """,
                "account(s) whose balance differs from their entries",
                cancellationToken),
            await CountCheckAsync(
                transaction,
                "last_balance_after_matches",
                "Every account's latest entry shows the account's current balance as its balance-after.",
                """
                SELECT COUNT(*)
                  FROM accounts a
                  JOIN LATERAL (SELECT e.balance_after
                                  FROM journal_entries e
                                 WHERE e.account_id = a.id
                                 ORDER BY e.id DESC
                                 LIMIT 1) last ON TRUE
                 WHERE last.balance_after <> a.balance
                """,
                "account(s) whose last balance-after differs from their balance",
                cancellationToken),
            await CountCheckAsync(
                transaction,
                "entry_sequences_gapless",
                "Every account's entries are numbered 1, 2, 3 ... with no gaps, and its counter equals its entry count.",
                """
                SELECT COUNT(*)
                  FROM accounts a
                  LEFT JOIN (SELECT account_id, COUNT(*) AS entries, MAX(account_sequence) AS highest
                               FROM journal_entries
                              GROUP BY account_id) e ON e.account_id = a.id
                 WHERE a.entry_sequence <> COALESCE(e.entries, 0)
                    OR COALESCE(e.highest, 0) <> COALESCE(e.entries, 0)
                """,
                "account(s) with a gap or a wrong counter in their entry sequence",
                cancellationToken),
            await CountCheckAsync(
                transaction,
                "held_matches_active_holds",
                "Every account's held amount equals the total of its active holds.",
                """
                SELECT COUNT(*)
                  FROM accounts a
                  LEFT JOIN (SELECT account_id, SUM(amount) AS held
                               FROM holds
                              WHERE status = 'Active'
                              GROUP BY account_id) h ON h.account_id = a.id
                 WHERE a.held <> COALESCE(h.held, 0)
                """,
                "account(s) whose held amount differs from their active holds",
                cancellationToken),
            await CountCheckAsync(
                transaction,
                "no_negative_balances",
                "No wallet has a negative balance, negative held amount or negative available balance.",
                "SELECT COUNT(*) FROM accounts WHERE held < 0 OR (NOT allow_negative AND (balance < 0 OR balance - held < 0))",
                "account(s) below zero",
                cancellationToken),
            await TrialBalanceAsync(transaction, cancellationToken),
            await CountCheckAsync(
                transaction,
                "journals_audited",
                "Every journal has its journal.posted row in the audit log.",
                """
                SELECT COUNT(*)
                  FROM journal_transactions j
                 WHERE NOT EXISTS (SELECT 1
                                     FROM audit_log a
                                    WHERE a.subject_type = 'journal'
                                      AND a.subject_id = j.id::text
                                      AND a.event_type = 'journal.posted')
                """,
                "journal(s) missing from the audit log",
                cancellationToken),
            await AuditChainAsync(checkedAt, cancellationToken),
        ];

        await transaction.RollbackAsync(cancellationToken);

        return new InvariantReport(checkedAt, checks.TrueForAll(check => check.Passed), stopwatch.ElapsedMilliseconds, checks);
    }

    private static string Rupees(long paisa) => Money.FromPaisa(paisa).ToDisplayString();

    private async Task<InvariantCheck> DebitsEqualCreditsAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var row = await QueryAsync(
            transaction,
            """
            SELECT COALESCE(SUM(amount) FILTER (WHERE direction = 'Debit'), 0),
                   COALESCE(SUM(amount) FILTER (WHERE direction = 'Credit'), 0),
                   COUNT(*)
              FROM journal_entries
            """,
            cancellationToken);
        var (debits, credits, entries) = (row[0], row[1], row[2]);

        return new InvariantCheck(
            "debits_equal_credits",
            "Across the whole ledger, total debits equal total credits.",
            debits == credits,
            debits == credits ? 0 : 1,
            $"{entries.ToString("N0", CultureInfo.InvariantCulture)} entries; debits {Rupees(debits)}, credits {Rupees(credits)}.");
    }

    private async Task<InvariantCheck> TrialBalanceAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        // Credit-normal balances (wallets, fees, tax) minus debit-normal ones (bank float) must be zero:
        // every rupee in a wallet is backed by a rupee in the bank.
        var row = await QueryAsync(
            transaction,
            """
            SELECT COALESCE(SUM(balance) FILTER (WHERE normal_balance = 'Credit'), 0),
                   COALESCE(SUM(balance) FILTER (WHERE normal_balance = 'Debit'), 0)
              FROM accounts
            """,
            cancellationToken);
        var (creditSide, debitSide) = (row[0], row[1]);

        return new InvariantCheck(
            "trial_balance_zero",
            "Credit-normal balances (wallets, fees, tax) equal debit-normal balances (bank float).",
            creditSide == debitSide,
            creditSide == debitSide ? 0 : 1,
            $"Credit side {Rupees(creditSide)}, debit side {Rupees(debitSide)}.");
    }

    private async Task<InvariantCheck> AuditChainAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = await auditVerifier.VerifyAsync(cancellationToken);
        var maxAge = TimeSpan.FromSeconds(auditOptions.MaxUnsealedAgeSeconds);
        var stale = report.OldestPendingAt is { } oldest && now - oldest > maxAge;

        var detail = !report.Intact
            ? $"Broken at #{report.FirstBrokenSequence}: {report.Problem}"
            : stale
                ? $"{report.PendingCount} row(s) waiting to be sealed since {report.OldestPendingAt:O}; the sealer appears stuck."
                : $"{report.SealedCount.ToString("N0", CultureInfo.InvariantCulture)} sealed, {report.PendingCount} pending; head #{report.HeadSequence} {report.HeadHash}.";

        return new InvariantCheck(
            "audit_chain_intact",
            "Every sealed audit row matches its hash and links to the row before it, and nothing waits too long to be sealed.",
            report.Intact && !stale,
            report.Intact && !stale ? 0 : 1,
            detail);
    }

    private async Task<InvariantCheck> CountCheckAsync(
        IDbContextTransaction transaction, string name, string description, string sql, string what, CancellationToken cancellationToken)
    {
        var violations = (await QueryAsync(transaction, sql, cancellationToken))[0];
        return new InvariantCheck(name, description, violations == 0, violations, violations == 0 ? "OK." : $"{violations} {what}.");
    }

    /// <summary>Runs a query in the check's transaction and returns its first row as numbers.</summary>
    private async Task<long[]> QueryAsync(IDbContextTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return [];
        }

        var values = new long[reader.FieldCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
        }

        return values;
    }
}
