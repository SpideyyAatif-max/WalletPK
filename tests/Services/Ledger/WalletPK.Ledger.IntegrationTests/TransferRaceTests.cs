using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using static WalletPK.Ledger.IntegrationTests.Infrastructure.LedgerTestKit;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>
/// M3.12 race tests through the real HTTP API: Redis wallet locks, fencing tokens, row versions,
/// idempotency and the database guards all working together under parallel load.
/// </summary>
[Collection(LedgerCollection.Name)]
public class TransferRaceTests(LedgerApiFactory factory)
{
    [Fact]
    public async Task Fifty_parallel_transfers_from_one_wallet_never_overspend()
    {
        // Rs. 10,000 in the wallet, 50 transfers of Rs. 300 (free) at the same instant: exactly 33 fit.
        var sender = await factory.CustomerAsync(rupees: 10_000);
        var recipients = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => factory.CustomerAsync()));
        var keys = Enumerable.Range(0, 50).Select(_ => NewKey()).ToArray();

        var responses = await Task.WhenAll(keys.Select((key, i) => sender.SendAsync(recipients[i % recipients.Length].Phone, 30_000, key)));

        var succeeded = responses.Count(response => response.StatusCode == HttpStatusCode.OK);
        var refused = responses.Where(response => response.StatusCode != HttpStatusCode.OK).ToList();
        succeeded.ShouldBe(33);
        foreach (var response in refused)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe("ledger.insufficient_funds");
        }

        (await factory.BalanceAsync(sender.WalletId)).ShouldBe(10_000);           // Rs. 100 left
        var received = await Task.WhenAll(recipients.Select(recipient => factory.BalanceAsync(recipient.WalletId)));
        received.Sum().ShouldBe(33 * 30_000);

        // Every refused transfer gave its limit reservation back; every successful one kept it.
        var refusedIds = keys.Where((_, i) => responses[i].StatusCode != HttpStatusCode.OK).Select(key => WalletService.TransactionIdFor(sender.Id, key));
        refusedIds.ShouldAllBe(id => factory.Limits.Released.ContainsKey(id));

        foreach (var response in responses)
        {
            response.Dispose();
        }

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Transfers_in_both_directions_at_once_do_not_deadlock_and_conserve_money()
    {
        // A pays B and B pays A, 25 times each, all in parallel. Locks are always taken in the same
        // (sorted) order, so the two directions can never wait on each other forever.
        var alice = await factory.CustomerAsync(rupees: 5_000);
        var bilal = await factory.CustomerAsync(rupees: 5_000);

        var transfers = Enumerable.Range(0, 50).Select(i => i % 2 == 0
            ? alice.SendAsync(bilal.Phone, 10_000 + i, NewKey())
            : bilal.SendAsync(alice.Phone, 10_000 + i, NewKey()));
        var responses = await Task.WhenAll(transfers);

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var aliceSent = Enumerable.Range(0, 50).Where(i => i % 2 == 0).Sum(i => 10_000L + i);
        var bilalSent = Enumerable.Range(0, 50).Where(i => i % 2 == 1).Sum(i => 10_000L + i);
        (await factory.BalanceAsync(alice.WalletId)).ShouldBe(500_000 - aliceSent + bilalSent);
        (await factory.BalanceAsync(bilal.WalletId)).ShouldBe(500_000 - bilalSent + aliceSent);

        foreach (var response in responses)
        {
            response.Dispose();
        }

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Twenty_parallel_retries_of_one_request_move_the_money_once()
    {
        // A double-tapping, retrying client: the same Idempotency-Key twenty times at once.
        var sender = await factory.CustomerAsync(rupees: 1_000);
        var recipient = await factory.CustomerAsync();
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sender.SendAsync(recipient.Phone, 50_000, key)));

        // Each answer is the receipt (first or replayed) or "still in progress, retry".
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);
        responses.ShouldContain(response => response.StatusCode == HttpStatusCode.OK);
        (await factory.BalanceAsync(recipient.WalletId)).ShouldBe(50_000);
        (await factory.BalanceAsync(sender.WalletId)).ShouldBe(50_000);

        var transactionId = WalletService.TransactionIdFor(sender.Id, key);
        var journals = await factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().JournalTransactions
            .CountAsync(journal => journal.Id == transactionId, Cancel));
        journals.ShouldBe(1);

        foreach (var response in responses)
        {
            response.Dispose();
        }

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Ten_parallel_captures_and_releases_of_one_hold_settle_it_exactly_once()
    {
        // Found by the property test: a hold read before taking the wallet lock was stale by the time the
        // lock was granted, so a second release tried to give back money that was already given back.
        var wallet = await factory.CustomerAsync(rupees: 1_000);
        var hold = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().PlaceAsync(
            new PlaceHoldCommand(wallet.WalletId, Money.FromRupees(400), "Bill payment", null, TimeSpan.FromMinutes(30)), Cancel));
        var holdId = hold.Value.HoldId;

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var released = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().ReleaseAsync(holdId, Cancel));
                return (Succeeded: released.IsSuccess, Captured: false, Code: released.IsFailure ? released.Error.Code : null);
            }

            var amount = Money.FromRupees(300);
            var captured = await factory.InScopeAsync(services => services.GetRequiredService<HoldService>().CaptureAsync(
                new CaptureHoldCommand(holdId, TransactionId.New(), amount, JournalType.BillPayment, "Bill payment",
                    [PostingLine.Credit(AccountRef.System(ChartOfAccounts.BillerSettlement), amount)]),
                Cancel));
            return (Succeeded: captured.IsSuccess, Captured: true, Code: captured.IsFailure ? captured.Error.Code : null);
        }));

        attempts.Count(attempt => attempt.Succeeded).ShouldBe(1);
        attempts.Where(attempt => !attempt.Succeeded).ShouldAllBe(attempt => attempt.Code == "ledger.hold_not_active");

        var account = await factory.AccountAsync(wallet.WalletId);
        account.Held.ShouldBe(0);
        account.Balance.ShouldBe(attempts.Single(attempt => attempt.Succeeded).Captured ? 70_000 : 100_000);

        await factory.ShouldBeHealthyAsync();
    }

    [Fact]
    public async Task Twenty_postings_to_the_same_hot_system_shard_at_once_all_succeed()
    {
        // Found by the property test: postings racing for one bank-float shard both took the same entry
        // number; the duplicate-key error was mistaken for "already posted" and the payment failed.
        // Pick 20 transaction IDs that all land on one shard, then post them at the same instant.
        var target = ChartOfAccounts.ShardIdFor(ChartOfAccounts.BankFloat, TransactionId.New());
        var ids = Enumerable.Range(0, 10_000).Select(_ => TransactionId.New())
            .Where(id => ChartOfAccounts.ShardIdFor(ChartOfAccounts.BankFloat, id) == target)
            .Take(20)
            .ToList();
        var wallets = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => factory.CustomerAsync()));
        var before = await factory.AccountAsync(target);

        var results = await Task.WhenAll(ids.Select((id, i) => factory.InScopeAsync(services =>
            services.GetRequiredService<PostingService>().PostAsync(
                new PostJournalCommand(id, JournalType.CashIn, "Cash-in at agent", null,
                [
                    PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(100)),
                    PostingLine.Credit(AccountRef.Account(wallets[i].WalletId), Money.FromRupees(100)),
                ]),
                Cancel))));

        results.ShouldAllBe(result => result.IsSuccess);
        var after = await factory.AccountAsync(target);
        after.Balance.ShouldBe(before.Balance + 20 * 10_000);
        after.EntrySequence.ShouldBe(before.EntrySequence + 20);

        await factory.ShouldBeHealthyAsync();
    }
}
