using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;

namespace WalletPK.Ledger.IntegrationTests;

[Collection(LedgerCollection.Name)]
public class HoldsAndConcurrencyTests(LedgerApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Opens a wallet and cashes in <paramref name="rupees"/>.</summary>
    private async Task<AccountId> FundedWalletAsync(decimal rupees)
    {
        using var client = factory.CreateClient();
        var wallet = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, "Test", factory.Clock.GetUtcNow());
        await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<LedgerDbContext>();
            db.Accounts.Add(wallet);
            return await db.SaveChangesAsync(Cancel);
        });

        if (rupees > 0)
        {
            var funded = await InScopeAsync(services => services.GetRequiredService<PostingService>().PostAsync(
                new PostJournalCommand(TransactionId.New(), JournalType.CashIn, "Funding", null,
                [
                    PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(rupees)),
                    PostingLine.Credit(AccountRef.Account(wallet.Id), Money.FromRupees(rupees)),
                ]),
                Cancel));
            funded.IsSuccess.ShouldBeTrue();
        }

        return wallet.Id;
    }

    private Task<Account> AccountAsync(AccountId id) =>
        InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Accounts.AsNoTracking().SingleAsync(account => account.Id == id, Cancel));

    private Task<WalletPK.Common.Results.Result<HoldView>> PlaceAsync(AccountId wallet, decimal rupees, TimeSpan? lifetime = null) =>
        InScopeAsync(services => services.GetRequiredService<HoldService>().PlaceAsync(
            new PlaceHoldCommand(wallet, Money.FromRupees(rupees), "Bank transfer", "RAAST-TEST", lifetime ?? TimeSpan.FromMinutes(30)),
            Cancel));

    private Task<WalletPK.Common.Results.Result<PostedJournal>> SendAsync(AccountId from, AccountId to, decimal rupees) =>
        InScopeAsync(services => services.GetRequiredService<PostingService>().PostAsync(
            new PostJournalCommand(TransactionId.New(), JournalType.P2PTransfer, "P2P", null,
            [
                PostingLine.Debit(AccountRef.Account(from), Money.FromRupees(rupees)),
                PostingLine.Credit(AccountRef.Account(to), Money.FromRupees(rupees)),
            ]),
            Cancel));

    // ---------------- M3.5 Holds ----------------

    [Fact]
    public async Task A_partial_capture_pays_the_destination_and_returns_the_rest()
    {
        var ali = await FundedWalletAsync(1_000);
        var biller = await FundedWalletAsync(0);
        var hold = (await PlaceAsync(ali, 600)).Value;

        hold.AvailableAfter.ShouldBe(Money.FromRupees(400));
        (await AccountAsync(ali)).Balance.ShouldBe(100_000);

        var transactionId = TransactionId.New();
        var captured = await InScopeAsync(services => services.GetRequiredService<HoldService>().CaptureAsync(
            new CaptureHoldCommand(hold.HoldId, transactionId, Money.FromRupees(450), JournalType.BillPayment, "Electricity bill",
            [
                PostingLine.Credit(AccountRef.Account(biller), Money.FromRupees(440)),
                PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), Money.FromRupees(10)),
            ]),
            Cancel));

        captured.Value.TransactionId.ShouldBe(transactionId);
        var aliAfter = await AccountAsync(ali);
        aliAfter.Balance.ShouldBe(55_000);
        aliAfter.Held.ShouldBe(0);
        (await AccountAsync(biller)).Balance.ShouldBe(44_000);

        var stored = await InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Holds.AsNoTracking().SingleAsync(h => h.Id == hold.HoldId, Cancel));
        stored.Status.ShouldBe(HoldStatus.Captured);
        stored.CapturedAmount.ShouldBe(45_000);
        stored.CaptureTransactionId.ShouldBe(transactionId);
    }

    [Fact]
    public async Task Held_money_cannot_be_spent_by_another_payment()
    {
        var ali = await FundedWalletAsync(1_000);
        var sara = await FundedWalletAsync(0);
        await PlaceAsync(ali, 800);

        var tooMuch = await SendAsync(ali, sara, 300);
        var fits = await SendAsync(ali, sara, 200);

        tooMuch.Error.Code.ShouldBe("ledger.insufficient_funds");
        fits.IsSuccess.ShouldBeTrue();
        (await AccountAsync(ali)).Available.ShouldBe(0);
    }

    [Fact]
    public async Task Releasing_returns_everything_and_a_hold_settles_only_once()
    {
        var ali = await FundedWalletAsync(500);
        var hold = (await PlaceAsync(ali, 500)).Value;

        var released = await InScopeAsync(services => services.GetRequiredService<HoldService>().ReleaseAsync(hold.HoldId, Cancel));
        var again = await InScopeAsync(services => services.GetRequiredService<HoldService>().ReleaseAsync(hold.HoldId, Cancel));

        released.Value.Status.ShouldBe(HoldStatus.Released);
        released.Value.AvailableAfter.ShouldBe(Money.FromRupees(500));
        again.Error.Code.ShouldBe("ledger.hold_not_active");
    }

    [Fact]
    public async Task The_sweeper_returns_money_from_expired_holds()
    {
        var ali = await FundedWalletAsync(300);
        var hold = (await PlaceAsync(ali, 300, TimeSpan.FromMinutes(1))).Value;

        var tooEarly = await InScopeAsync(services => services.GetRequiredService<HoldService>().ExpireDueAsync(500, Cancel));
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var swept = await InScopeAsync(services => services.GetRequiredService<HoldService>().ExpireDueAsync(500, Cancel));

        tooEarly.ShouldBe(0);
        swept.ShouldBeGreaterThanOrEqualTo(1);
        (await AccountAsync(ali)).Available.ShouldBe(30_000);
        (await InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Holds.AsNoTracking().SingleAsync(h => h.Id == hold.HoldId, Cancel)))
            .Status.ShouldBe(HoldStatus.Expired);
    }

    // ---------------- M3.6 Concurrency ----------------

    [Fact]
    public async Task Fifty_parallel_debits_never_overdraw_the_wallet()
    {
        var ali = await FundedWalletAsync(100);
        var sara = await FundedWalletAsync(0);

        // 50 payments of Rs 10 against Rs 100, all at once: exactly 10 may succeed.
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => SendAsync(ali, sara, 10), Cancel)));

        results.Count(result => result.IsSuccess).ShouldBe(10);
        results.Where(result => result.IsFailure).ShouldAllBe(result => result.Error.Code == "ledger.insufficient_funds");
        (await AccountAsync(ali)).Balance.ShouldBe(0);
        (await AccountAsync(sara)).Balance.ShouldBe(10_000);

        // And the history agrees with the cached balances.
        var entries = await InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().JournalEntries.AsNoTracking()
            .Where(entry => entry.AccountId == ali).OrderBy(entry => entry.Id).ToListAsync(Cancel));
        entries.Count.ShouldBe(11); // the funding credit + 10 debits
        entries[^1].BalanceAfter.ShouldBe(0);
    }

    [Fact]
    public async Task Transfers_in_both_directions_at_once_do_not_deadlock()
    {
        var ali = await FundedWalletAsync(1_000);
        var sara = await FundedWalletAsync(1_000);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => i % 2 == 0 ? SendAsync(ali, sara, 10) : SendAsync(sara, ali, 10), Cancel)));

        results.ShouldAllBe(result => result.IsSuccess);
        ((await AccountAsync(ali)).Balance + (await AccountAsync(sara)).Balance).ShouldBe(200_000);
    }

    [Fact]
    public async Task Each_new_lock_gets_a_higher_fencing_token_and_waiters_get_their_turn()
    {
        var wallet = await FundedWalletAsync(0);
        var locks = factory.Services.GetRequiredService<IAccountLockService>();

        var first = (await locks.TryAcquireAsync([wallet], Cancel)).ShouldNotBeNull();
        var waiting = Task.Run(() => locks.TryAcquireAsync([wallet], Cancel), Cancel);
        await Task.Delay(200, Cancel);
        waiting.IsCompleted.ShouldBeFalse();

        var firstToken = first.FencingTokens[wallet];
        await first.DisposeAsync();
        await using var second = (await waiting).ShouldNotBeNull();

        second.FencingTokens[wallet].ShouldBeGreaterThan(firstToken);
    }

    [Fact]
    public async Task The_database_refuses_a_write_from_an_older_lock()
    {
        var wallet = await FundedWalletAsync(10);
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using (var setToken = new NpgsqlCommand("UPDATE accounts SET lock_token = 100 WHERE id = @id", connection))
        {
            setToken.Parameters.AddWithValue("id", wallet.Value);
            await setToken.ExecuteNonQueryAsync(Cancel);
        }

        await using var stale = new NpgsqlCommand("UPDATE accounts SET lock_token = 99 WHERE id = @id", connection);
        stale.Parameters.AddWithValue("id", wallet.Value);

        var error = await Should.ThrowAsync<PostgresException>(() => stale.ExecuteNonQueryAsync(Cancel));
        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("Stale wallet lock");
    }

    [Fact]
    public void Lock_contention_settings_leave_room_for_the_race_tests() =>
        WalletConcurrency.MaxAttempts.ShouldBe(3);
}
