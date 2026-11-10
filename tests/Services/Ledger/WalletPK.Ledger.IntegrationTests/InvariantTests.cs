using System.Net;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M3.11: the invariant checker passes on real activity and catches each kind of corruption. Corruption is
/// made behind the application's back (as a bad hand-written SQL fix would) and undone in the same test.
/// </summary>
[Collection(LedgerCollection.Name)]
public class InvariantTests(LedgerApiFactory factory)
{
    private static readonly string[] _allChecks =
    [
        "debits_equal_credits", "journals_balanced", "balances_match_entries", "last_balance_after_matches",
        "entry_sequences_gapless", "held_matches_active_holds", "no_negative_balances", "trial_balance_zero", "journals_audited", "audit_chain_intact",
    ];

    private async Task<string[]> FailedChecksAsync() =>
        [.. (await factory.CheckInvariantsAsync()).Failures.Select(failure => failure.Name)];

    [Fact]
    public async Task All_checks_pass_after_real_activity()
    {
        var sender = await factory.CustomerAsync(rupees: 5_000);
        var recipient = await factory.CustomerAsync();
        using var sent = await sender.SendAsync(recipient.Phone, 250_000, NewKey());
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = await factory.CheckInvariantsAsync();

        report.Checks.Select(check => check.Name).ShouldBe(_allChecks);
        report.Healthy.ShouldBeTrue(string.Join("; ", report.Failures.Select(failure => $"{failure.Name}: {failure.Detail}")));
    }

    [Fact]
    public async Task Operations_staff_can_run_the_checks_over_http_and_customers_cannot()
    {
        await factory.SealAuditAsync();
        using var ops = factory.CreateClientFor(UserId.New(), phoneNumber: null, role: "finance");
        using var customer = factory.CreateClientFor(UserId.New(), NewPhone());
        using var anonymous = factory.CreateClient();

        using var invariants = await ops.GetAsync(new Uri("/api/ledger/invariants", UriKind.Relative), Cancel);
        using var audit = await ops.GetAsync(new Uri("/api/ledger/audit", UriKind.Relative), Cancel);
        using var forbidden = await customer.GetAsync(new Uri("/api/ledger/invariants", UriKind.Relative), Cancel);
        using var unauthorized = await anonymous.GetAsync(new Uri("/api/ledger/audit", UriKind.Relative), Cancel);

        invariants.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await JsonAsync(invariants);
        report.GetProperty("healthy").GetBoolean().ShouldBeTrue();
        report.GetProperty("checks").GetArrayLength().ShouldBe(_allChecks.Length);

        audit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(audit)).GetProperty("intact").GetBoolean().ShouldBeTrue();

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_balance_edited_by_hand_is_caught_three_ways()
    {
        var wallet = await factory.CustomerAsync(rupees: 100);

        // "Fixing" a balance with SQL instead of posting a journal: Rs. 1 appears from nowhere.
        await factory.ExecuteSqlAsync("UPDATE accounts SET balance = balance + 100 WHERE id = @id", ("id", wallet.WalletId.Value));
        try
        {
            (await FailedChecksAsync()).ShouldBe(["balances_match_entries", "last_balance_after_matches", "trial_balance_zero"], ignoreOrder: true);
        }
        finally
        {
            await factory.ExecuteSqlAsync("UPDATE accounts SET balance = balance - 100 WHERE id = @id", ("id", wallet.WalletId.Value));
        }

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_held_amount_without_a_hold_is_caught()
    {
        var wallet = await factory.CustomerAsync(rupees: 100);

        await factory.ExecuteSqlAsync("UPDATE accounts SET held = held + 500 WHERE id = @id", ("id", wallet.WalletId.Value));
        try
        {
            (await FailedChecksAsync()).ShouldBe(["held_matches_active_holds"]);
        }
        finally
        {
            await factory.ExecuteSqlAsync("UPDATE accounts SET held = held - 500 WHERE id = @id", ("id", wallet.WalletId.Value));
        }

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_journal_written_around_the_application_is_caught_as_unaudited()
    {
        // A balanced journal inserted straight into the database: it passes the database's own guards
        // (balanced, append-only) but nothing recorded who made it. It debits and credits the suspense
        // account by the same amount, so balances stay right and only the audit check can see it.
        var suspense = await factory.AccountAsync(ChartOfAccounts.IdFor(ChartOfAccounts.Suspense.Code, 0));
        var id = Guid.CreateVersion7();

        // Even the sequence numbers are right: someone who knew the schema well.
        await factory.ExecuteSqlAsync(
            """
            BEGIN;
            INSERT INTO journal_transactions (id, type, description, amount, currency, posted_at) VALUES (@id, 'Adjustment', 'manual fix', 100, 'PKR', now());
            INSERT INTO journal_entries (transaction_id, line_number, account_id, account_sequence, direction, amount, balance_after, posted_at)
                 VALUES (@id, 1, @account, @sequence + 1, 'Debit', 100, @balance, now()), (@id, 2, @account, @sequence + 2, 'Credit', 100, @balance, now());
            UPDATE accounts SET entry_sequence = @sequence + 2 WHERE id = @account;
            COMMIT;
            """,
            ("id", id), ("account", suspense.Id.Value), ("balance", suspense.Balance), ("sequence", suspense.EntrySequence));

        (await FailedChecksAsync()).ShouldBe(["journals_audited"]);

        // History cannot be deleted, so the gap is closed the honest way: record it in the audit log.
        await factory.ExecuteSqlAsync(
            "INSERT INTO audit_log (occurred_at, event_type, subject_type, subject_id, actor, payload) VALUES (now(), 'journal.posted', 'journal', @id, 'test:repair', '{}')",
            ("id", id.ToString()));
        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task A_sealer_that_stops_is_caught_once_rows_wait_too_long()
    {
        await factory.SealAuditAsync();
        await factory.CustomerAsync();

        // Rows are pending; move the clock past the five-minute limit without sealing.
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        var report = await factory.InScopeAsync(services => services.GetRequiredService<ILedgerInvariantChecker>().CheckAsync(Cancel));

        var audit = report.Checks.Single(check => check.Name == "audit_chain_intact");
        audit.Passed.ShouldBeFalse();
        audit.Detail.ShouldContain("sealer appears stuck");

        await factory.ShouldBeHealthyAsync();
    }
}
