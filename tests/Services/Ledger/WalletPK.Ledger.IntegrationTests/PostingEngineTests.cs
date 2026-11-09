using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;

namespace WalletPK.Ledger.IntegrationTests;

[Collection(LedgerCollection.Name)]
public class PostingEngineTests(LedgerApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<AccountId> OpenWalletAsync(string name)
    {
        using var client = factory.CreateClient(); // starts the service (and migrates) on first use
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var wallet = Account.OpenWallet(UserId.New(), AccountType.CustomerWallet, name, DateTimeOffset.UtcNow);
        db.Accounts.Add(wallet);
        await db.SaveChangesAsync(Cancel);
        return wallet.Id;
    }

    private async Task<WalletPK.Common.Results.Result<PostedJournal>> PostAsync(PostJournalCommand command)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PostingService>().PostAsync(command, Cancel);
    }

    private static PostJournalCommand CashIn(AccountId wallet, decimal rupees, TransactionId? id = null) =>
        new(id ?? TransactionId.New(), JournalType.CashIn, "Cash-in from bank", null,
        [
            PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(rupees)),
            PostingLine.Credit(AccountRef.Account(wallet), Money.FromRupees(rupees)),
        ]);

    private async Task<long> BalanceAsync(AccountId id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await db.Accounts.Where(account => account.Id == id).Select(account => account.Balance).SingleAsync(Cancel);
    }

    [Fact]
    public async Task The_migration_creates_every_system_account_shard_of_the_chart()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var system = await db.Accounts.Where(account => account.SystemCode != null).ToListAsync(Cancel);

        system.Count.ShouldBe(21);
        foreach (var definition in ChartOfAccounts.SystemAccounts)
        {
            var shards = system.Where(account => account.SystemCode == definition.Code).OrderBy(account => account.ShardIndex).ToList();
            shards.Count.ShouldBe(definition.Shards);
            shards.Select(account => account.Id).ShouldBe(Enumerable.Range(0, definition.Shards).Select(shard => ChartOfAccounts.IdFor(definition.Code, shard)));
            shards.ShouldAllBe(account => account.NormalBalance == definition.NormalBalance && account.AllowNegative);
        }
    }

    [Fact]
    public async Task A_cash_in_and_a_transfer_with_fee_are_saved_with_balances_entries_and_an_outbox_event()
    {
        var ali = await OpenWalletAsync("Ali");
        var sara = await OpenWalletAsync("Sara");
        (await PostAsync(CashIn(ali, 1_000))).IsSuccess.ShouldBeTrue();

        var transferId = TransactionId.New();
        var transfer = await PostAsync(new PostJournalCommand(transferId, JournalType.P2PTransfer, "Ali to Sara", "P2P-1",
        [
            PostingLine.Debit(AccountRef.Account(ali), Money.FromRupees(510)),
            PostingLine.Credit(AccountRef.Account(sara), Money.FromRupees(500)),
            PostingLine.Credit(AccountRef.System(ChartOfAccounts.FeeIncome), Money.FromRupees(10)),
        ]));

        transfer.Value.AlreadyPosted.ShouldBeFalse();
        (await BalanceAsync(ali)).ShouldBe(49_000);
        (await BalanceAsync(sara)).ShouldBe(50_000);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var entries = await db.JournalEntries.Where(entry => entry.TransactionId == transferId).OrderBy(entry => entry.LineNumber).ToListAsync(Cancel);
        entries.Select(entry => (entry.Direction, entry.Amount, entry.BalanceAfter)).ShouldBe(new[]
        {
            (EntryDirection.Debit, 51_000L, 49_000L),
            (EntryDirection.Credit, 50_000L, 50_000L),
            (EntryDirection.Credit, 1_000L, entries[2].BalanceAfter),
        });
        entries[2].AccountId.ShouldBe(ChartOfAccounts.ShardIdFor(ChartOfAccounts.FeeIncome, transferId));

        var outbox = await db.OutboxMessages.SingleAsync(message => message.PartitionKey == transferId.ToString(), Cancel);
        outbox.EventType.ShouldBe("ledger.journal_posted");
        outbox.Topic.ShouldBe("ledger.events");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("amountPaisa").GetInt64().ShouldBe(51_000);
        payload.RootElement.GetProperty("entries").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task An_overdraft_is_refused_and_nothing_is_written()
    {
        var ali = await OpenWalletAsync("Ali");
        var sara = await OpenWalletAsync("Sara");
        await PostAsync(CashIn(ali, 100));
        var id = TransactionId.New();

        var result = await PostAsync(new PostJournalCommand(id, JournalType.P2PTransfer, "Too much", null,
        [
            PostingLine.Debit(AccountRef.Account(ali), Money.FromRupees(100.01m)),
            PostingLine.Credit(AccountRef.Account(sara), Money.FromRupees(100.01m)),
        ]));

        result.Error.Code.ShouldBe("ledger.insufficient_funds");
        (await BalanceAsync(ali)).ShouldBe(10_000);
        (await BalanceAsync(sara)).ShouldBe(0);
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().JournalTransactions.AnyAsync(journal => journal.Id == id, Cancel)).ShouldBeFalse();
    }

    [Fact]
    public async Task Posting_the_same_transaction_twice_moves_the_money_once()
    {
        var ali = await OpenWalletAsync("Ali");
        var id = TransactionId.New();

        var first = await PostAsync(CashIn(ali, 250, id));
        var second = await PostAsync(CashIn(ali, 250, id));

        first.Value.AlreadyPosted.ShouldBeFalse();
        second.Value.AlreadyPosted.ShouldBeTrue();
        second.Value.Entries.Count.ShouldBe(2);
        (await BalanceAsync(ali)).ShouldBe(25_000);
    }

    [Fact]
    public async Task Busy_system_accounts_are_spread_over_their_shards_and_still_add_up()
    {
        var wallet = await OpenWalletAsync("Merchant");
        for (var i = 0; i < 24; i++)
        {
            (await PostAsync(CashIn(wallet, 10))).IsSuccess.ShouldBeTrue();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var walletEntries = db.JournalEntries.Where(entry => entry.AccountId == wallet).Select(entry => entry.TransactionId);
        var shardsUsed = await db.JournalEntries
            .Where(entry => walletEntries.Contains(entry.TransactionId) && entry.AccountId != wallet)
            .Select(entry => entry.AccountId)
            .Distinct()
            .CountAsync(Cancel);

        shardsUsed.ShouldBeGreaterThan(1);
        (await BalanceAsync(wallet)).ShouldBe(24 * 1_000);
    }

    [Fact]
    public async Task Across_the_whole_ledger_debits_equal_credits()
    {
        var ali = await OpenWalletAsync("Ali");
        await PostAsync(CashIn(ali, 75));

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(
            "SELECT (COALESCE(SUM(amount) FILTER (WHERE direction = 'Debit'), 0) - COALESCE(SUM(amount) FILTER (WHERE direction = 'Credit'), 0))::bigint FROM journal_entries",
            connection);

        ((long)(await command.ExecuteScalarAsync(Cancel))!).ShouldBe(0);
    }
}
