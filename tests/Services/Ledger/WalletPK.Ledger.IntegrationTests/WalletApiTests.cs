using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.IntegrationTests.Infrastructure;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>M3.8 wallet APIs end to end over HTTP: real auth, Postgres, Redis; fake Identity limits.</summary>
[Collection(LedgerCollection.Name)]
public class WalletApiTests(LedgerApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private sealed record Customer(UserId Id, string Phone, AccountId WalletId, HttpClient Client);

    private static string NewPhone() => $"+923{Random.Shared.Next(0, 1_000_000_000):D9}";

    private static string NewKey() => Guid.NewGuid().ToString("N");

    /// <summary>Signs up a customer, opens their wallet over the API and cashes in <paramref name="rupees"/>.</summary>
    private async Task<Customer> CustomerAsync(decimal rupees = 0)
    {
        var userId = UserId.New();
        var phone = NewPhone();
        var client = factory.CreateClientFor(userId, phone);

        using var opened = await client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel);
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        var walletId = AccountId.Parse((await JsonAsync(opened)).GetProperty("walletId").GetString()!, null);

        if (rupees > 0)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var funded = await scope.ServiceProvider.GetRequiredService<PostingService>().PostAsync(
                new PostJournalCommand(TransactionId.New(), JournalType.CashIn, "Cash-in at agent", null,
                [
                    PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(rupees)),
                    PostingLine.Credit(AccountRef.Account(walletId), Money.FromRupees(rupees)),
                ]),
                Cancel);
            funded.IsSuccess.ShouldBeTrue();
        }

        return new Customer(userId, phone, walletId, client);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).Clone();

    private static long Paisa(JsonElement element, string property) => element.GetProperty(property).GetProperty("minorUnits").GetInt64();

    private static async Task<HttpResponseMessage> SendAsync(Customer from, string? to, long amountPaisa, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wallets/transfers")
        {
            Content = JsonContent.Create(new { toWalletNumber = to, amountPaisa }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await from.Client.SendAsync(request, Cancel);
    }

    private async Task<long> BalanceAsync(AccountId id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Accounts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id, Cancel);
        return account.Balance;
    }

    private static async Task<JsonElement> ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        var problem = await JsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(code);
        return problem;
    }

    [Fact]
    public async Task Wallet_endpoints_require_an_access_token()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Opening_a_wallet_is_idempotent_and_numbers_it_with_the_mobile_number()
    {
        var userId = UserId.New();
        var phone = NewPhone();
        using var client = factory.CreateClientFor(userId, phone);

        using var first = await client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel);
        using var second = await client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel);
        using var me = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await JsonAsync(first);
        (await JsonAsync(second)).GetProperty("walletId").GetString().ShouldBe(created.GetProperty("walletId").GetString());

        var wallet = await JsonAsync(me);
        wallet.GetProperty("walletNumber").GetString().ShouldBe(phone);
        wallet.GetProperty("type").GetString().ShouldBe("CustomerWallet");
        wallet.GetProperty("status").GetString().ShouldBe("Active");
        Paisa(wallet, "balance").ShouldBe(0);
        Paisa(wallet, "available").ShouldBe(0);
    }

    [Fact]
    public async Task A_token_without_a_mobile_number_cannot_open_a_wallet()
    {
        using var client = factory.CreateClientFor(UserId.New(), phoneNumber: null);

        using var response = await client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "wallet.phone_number_required");
    }

    [Fact]
    public async Task Reading_a_wallet_that_does_not_exist_is_404()
    {
        using var client = factory.CreateClientFor(UserId.New(), NewPhone());

        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        await ShouldBeProblemAsync(response, HttpStatusCode.NotFound, "wallet.not_found");
    }

    [Fact]
    public async Task Quote_shows_fee_and_tax_on_the_fee()
    {
        var customer = await CustomerAsync();

        using var paid = await customer.Client.GetAsync(new Uri("/api/wallets/transfers/quote?amountPaisa=150000", UriKind.Relative), Cancel);
        using var free = await customer.Client.GetAsync(new Uri("/api/wallets/transfers/quote?amountPaisa=50000", UriKind.Relative), Cancel);
        using var invalid = await customer.Client.GetAsync(new Uri("/api/wallets/transfers/quote?amountPaisa=0", UriKind.Relative), Cancel);

        var quote = await JsonAsync(paid);
        Paisa(quote, "fee").ShouldBe(750);       // 0.5% of Rs. 1,500
        Paisa(quote, "tax").ShouldBe(120);       // 16% of Rs. 7.50
        Paisa(quote, "totalDebit").ShouldBe(150_870);
        (await JsonAsync(free)).GetProperty("isFree").GetBoolean().ShouldBeTrue();
        await ShouldBeProblemAsync(invalid, HttpStatusCode.BadRequest, "wallet.amount_invalid");
    }

    [Fact]
    public async Task Transfer_moves_money_charges_fee_and_tax_and_reserves_the_limit()
    {
        var sender = await CustomerAsync(rupees: 5_000);
        var recipient = await CustomerAsync();
        var key = NewKey();

        // The recipient's number in local format, as a customer would type it.
        using var response = await SendAsync(sender, "0" + recipient.Phone[3..], 150_000, key);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipt = await JsonAsync(response);
        Paisa(receipt, "amount").ShouldBe(150_000);
        Paisa(receipt, "fee").ShouldBe(750);
        Paisa(receipt, "tax").ShouldBe(120);
        Paisa(receipt, "totalDebited").ShouldBe(150_870);
        Paisa(receipt, "balanceAfter").ShouldBe(500_000 - 150_870);
        receipt.GetProperty("toWalletNumber").GetString().ShouldBe(PakistaniMobileNumber.Mask(recipient.Phone));

        (await BalanceAsync(sender.WalletId)).ShouldBe(500_000 - 150_870);
        (await BalanceAsync(recipient.WalletId)).ShouldBe(150_000);

        var transactionId = WalletService.TransactionIdFor(sender.Id, key);
        receipt.GetProperty("transactionId").GetString().ShouldBe(transactionId.ToString());
        factory.Limits.Reserved[transactionId].ShouldBe(Money.FromPaisa(150_000));

        // Fee to FEE_INCOME, tax to TAX_PAYABLE, all in one balanced journal.
        await using var scope = factory.Services.CreateAsyncScope();
        var entries = await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().JournalEntries.AsNoTracking()
            .Where(entry => entry.TransactionId == transactionId)
            .ToListAsync(Cancel);
        entries.Count.ShouldBe(4);
        entries.Single(entry => entry.AccountId == ChartOfAccounts.ShardIdFor(ChartOfAccounts.FeeIncome, transactionId)).Amount.ShouldBe(750);
        entries.Single(entry => entry.AccountId == ChartOfAccounts.ShardIdFor(ChartOfAccounts.TaxPayable, transactionId)).Amount.ShouldBe(120);
        entries.Where(entry => entry.Direction == EntryDirection.Debit).Sum(entry => entry.Amount)
            .ShouldBe(entries.Where(entry => entry.Direction == EntryDirection.Credit).Sum(entry => entry.Amount));
    }

    [Fact]
    public async Task Small_transfers_are_free()
    {
        var sender = await CustomerAsync(rupees: 1_000);
        var recipient = await CustomerAsync();

        using var response = await SendAsync(sender, recipient.Phone, 50_000, NewKey());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var receipt = await JsonAsync(response);
        Paisa(receipt, "fee").ShouldBe(0);
        Paisa(receipt, "tax").ShouldBe(0);
        (await BalanceAsync(sender.WalletId)).ShouldBe(50_000);
        (await BalanceAsync(recipient.WalletId)).ShouldBe(50_000);
    }

    [Fact]
    public async Task Retrying_with_the_same_key_replays_the_receipt_and_moves_money_once()
    {
        var sender = await CustomerAsync(rupees: 5_000);
        var recipient = await CustomerAsync();
        var key = NewKey();

        using var first = await SendAsync(sender, recipient.Phone, 200_000, key);
        using var retry = await SendAsync(sender, recipient.Phone, 200_000, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await retry.Content.ReadAsStringAsync(Cancel)).ShouldBe(await first.Content.ReadAsStringAsync(Cancel));
        (await BalanceAsync(recipient.WalletId)).ShouldBe(200_000);
    }

    [Fact]
    public async Task A_transfer_needs_an_idempotency_key()
    {
        var sender = await CustomerAsync(rupees: 100);
        var recipient = await CustomerAsync();

        using var response = await SendAsync(sender, recipient.Phone, 1_000, key: null);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "idempotency.key_required");
    }

    [Fact]
    public async Task Transfers_to_unknown_invalid_or_own_numbers_are_refused()
    {
        var sender = await CustomerAsync(rupees: 100);

        using var unknown = await SendAsync(sender, NewPhone(), 1_000, NewKey());
        using var invalid = await SendAsync(sender, "12345", 1_000, NewKey());
        using var self = await SendAsync(sender, sender.Phone, 1_000, NewKey());
        using var zero = await SendAsync(sender, NewPhone(), 0, NewKey());

        await ShouldBeProblemAsync(unknown, HttpStatusCode.NotFound, "wallet.recipient_not_found");
        await ShouldBeProblemAsync(invalid, HttpStatusCode.BadRequest, "wallet.recipient_invalid");
        await ShouldBeProblemAsync(self, HttpStatusCode.BadRequest, "wallet.self_transfer");
        await ShouldBeProblemAsync(zero, HttpStatusCode.BadRequest, "wallet.amount_invalid");
        (await BalanceAsync(sender.WalletId)).ShouldBe(10_000);
    }

    [Fact]
    public async Task A_transfer_over_the_limit_is_refused_with_the_reason()
    {
        var sender = await CustomerAsync(rupees: 100_000);
        var recipient = await CustomerAsync();
        factory.Limits.Refuse(sender.Id, "ExceedsDaily", "This would exceed your daily limit of Rs. 25,000.");

        using var response = await SendAsync(sender, recipient.Phone, 3_000_000, NewKey());

        var problem = await ShouldBeProblemAsync(response, HttpStatusCode.UnprocessableEntity, "wallet.limit_exceeded");
        problem.GetProperty("detail").GetString().ShouldBe("This would exceed your daily limit of Rs. 25,000.");
        (await BalanceAsync(sender.WalletId)).ShouldBe(10_000_000);
    }

    [Fact]
    public async Task Insufficient_balance_refuses_the_transfer_and_releases_the_limit()
    {
        var sender = await CustomerAsync(rupees: 1_000);
        var recipient = await CustomerAsync();
        var key = NewKey();

        // Rs. 1,001 from a Rs. 1,000 wallet (plus fee Rs. 5.01 and tax Rs. 0.80): the limit is reserved first,
        // then posting fails, so the reservation must be given back.
        using var response = await SendAsync(sender, recipient.Phone, 100_100, key);

        await ShouldBeProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ledger.insufficient_funds");
        factory.Limits.Released.ShouldContainKey(WalletService.TransactionIdFor(sender.Id, key));
        (await BalanceAsync(sender.WalletId)).ShouldBe(100_000);
        (await BalanceAsync(recipient.WalletId)).ShouldBe(0);
    }

    [Fact]
    public async Task When_limits_cannot_be_checked_the_payment_fails_closed_and_can_be_retried()
    {
        var sender = await CustomerAsync(rupees: 1_000);
        var recipient = await CustomerAsync();
        var key = NewKey();
        factory.Limits.MakeUnavailable(sender.Id);

        using var response = await SendAsync(sender, recipient.Phone, 10_000, key);

        await ShouldBeProblemAsync(response, HttpStatusCode.ServiceUnavailable, "wallet.limits_unavailable");
        (await BalanceAsync(sender.WalletId)).ShouldBe(100_000);

        // A 503 is not remembered by the idempotency layer, so the same request can be retried once Identity is back.
        factory.Limits.MakeAvailable(sender.Id);
        using var retry = await SendAsync(sender, recipient.Phone, 10_000, key);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BalanceAsync(recipient.WalletId)).ShouldBe(10_000);
    }

    [Fact]
    public async Task History_lists_newest_first_in_pages_with_who_was_paid()
    {
        var sender = await CustomerAsync(rupees: 1_000);
        var recipient = await CustomerAsync();
        foreach (var paisa in new long[] { 1_000, 2_000, 3_000 })
        {
            using var sent = await SendAsync(sender, recipient.Phone, paisa, NewKey());
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var page1 = await sender.Client.GetAsync(new Uri("/api/wallets/me/transactions?limit=2", UriKind.Relative), Cancel);
        var first = await JsonAsync(page1);
        var nextBefore = first.GetProperty("nextBefore").GetInt64();
        using var page2 = await sender.Client.GetAsync(new Uri($"/api/wallets/me/transactions?limit=2&before={nextBefore}", UriKind.Relative), Cancel);
        var second = await JsonAsync(page2);

        var items = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToList();
        items.Select(item => Paisa(item, "amount")).ShouldBe([3_000, 2_000, 1_000, 100_000]);
        items[0].GetProperty("title").GetString().ShouldBe($"Sent to {PakistaniMobileNumber.Mask(recipient.Phone)}");
        items[0].GetProperty("direction").GetString().ShouldBe("Out");
        Paisa(items[0], "balanceAfter").ShouldBe(94_000);
        items[3].GetProperty("title").GetString().ShouldBe("Cash-in");
        items[3].GetProperty("direction").GetString().ShouldBe("In");
        second.GetProperty("nextBefore").ValueKind.ShouldBe(JsonValueKind.Null);

        using var received = await recipient.Client.GetAsync(new Uri("/api/wallets/me/transactions", UriKind.Relative), Cancel);
        var recipientItems = (await JsonAsync(received)).GetProperty("items").EnumerateArray().ToList();
        recipientItems.Count.ShouldBe(3);
        recipientItems[0].GetProperty("title").GetString().ShouldBe($"Received from {PakistaniMobileNumber.Mask(sender.Phone)}");
    }
}
