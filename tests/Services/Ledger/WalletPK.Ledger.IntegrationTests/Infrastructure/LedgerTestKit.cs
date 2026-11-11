using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Persistence;

namespace WalletPK.Ledger.IntegrationTests.Infrastructure;

/// <summary>A signed-in customer with an open wallet.</summary>
public sealed record Customer(UserId Id, string Phone, AccountId WalletId, HttpClient Client);

/// <summary>Shared steps for Ledger integration tests: customers, cash-in, transfers, sealing and invariant checks.</summary>
public static class LedgerTestKit
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static string NewPhone() => $"+923{Random.Shared.Next(0, 1_000_000_000):D9}";

    public static string NewKey() => Guid.NewGuid().ToString("N");

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).Clone();
    }

    public static long Paisa(JsonElement element, string property) => element.GetProperty(property).GetProperty("minorUnits").GetInt64();

    public static async Task<T> InScopeAsync<T>(this LedgerApiFactory factory, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Signs up a customer, opens their wallet over the API and cashes in <paramref name="rupees"/>.</summary>
    public static async Task<Customer> CustomerAsync(this LedgerApiFactory factory, decimal rupees = 0)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var userId = UserId.New();
        var phone = NewPhone();
        var client = factory.CreateClientFor(userId, phone);

        using var opened = await client.PostAsync(new Uri("/api/wallets", UriKind.Relative), null, Cancel);
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        var walletId = AccountId.Parse((await JsonAsync(opened)).GetProperty("walletId").GetString()!, null);

        if (rupees > 0)
        {
            await factory.CashInAsync(walletId, rupees);
        }

        return new Customer(userId, phone, walletId, client);
    }

    /// <summary>Cash-in at an agent: debit the bank float, credit the wallet.</summary>
    public static async Task CashInAsync(this LedgerApiFactory factory, AccountId walletId, decimal rupees)
    {
        var funded = await factory.InScopeAsync(services => services.GetRequiredService<PostingService>().PostAsync(
            new PostJournalCommand(TransactionId.New(), JournalType.CashIn, "Cash-in at agent", null,
            [
                PostingLine.Debit(AccountRef.System(ChartOfAccounts.BankFloat), Money.FromRupees(rupees)),
                PostingLine.Credit(AccountRef.Account(walletId), Money.FromRupees(rupees)),
            ]),
            Cancel));
        funded.IsSuccess.ShouldBeTrue(funded.IsFailure ? funded.Error.ToString() : null);
    }

    /// <summary>POST /api/wallets/transfers as <paramref name="from"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(this Customer from, string? to, long amountPaisa, string? key)
    {
        ArgumentNullException.ThrowIfNull(from);
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

    /// <summary>GET /api/wallets/me/transactions{query} as <paramref name="customer"/>; must succeed.</summary>
    public static async Task<JsonElement> HistoryAsync(this Customer customer, string query = "")
    {
        ArgumentNullException.ThrowIfNull(customer);
        using var response = await customer.Client.GetAsync(new Uri($"/api/wallets/me/transactions{query}", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonAsync(response);
    }

    /// <summary>Waits until the history read model shows at least <paramref name="entries"/> entries for the customer.</summary>
    public static Task WaitForHistoryAsync(this Customer customer, int entries) =>
        KafkaTestKit.EventuallyAsync(
            async () => (await customer.HistoryAsync("?limit=100")).GetProperty("items").GetArrayLength() >= entries,
            because: $"The history read model never reached {entries} entries.");

    public static Task<Account> AccountAsync(this LedgerApiFactory factory, AccountId id) =>
        factory.InScopeAsync(services => services.GetRequiredService<LedgerDbContext>().Accounts.AsNoTracking()
            .SingleAsync(account => account.Id == id, Cancel));

    public static async Task<long> BalanceAsync(this LedgerApiFactory factory, AccountId id) => (await factory.AccountAsync(id)).Balance;

    /// <summary>Seals every pending audit row (the background sealer is off in tests).</summary>
    public static Task<int> SealAuditAsync(this LedgerApiFactory factory) =>
        factory.InScopeAsync(services => services.GetRequiredService<AuditSealer>().SealAllAsync(500, Cancel));

    /// <summary>Seals the audit log, then runs every invariant check.</summary>
    public static async Task<InvariantReport> CheckInvariantsAsync(this LedgerApiFactory factory)
    {
        await factory.SealAuditAsync();
        return await factory.InScopeAsync(services => services.GetRequiredService<ILedgerInvariantChecker>().CheckAsync(Cancel));
    }

    /// <summary>Asserts every invariant holds, listing the failures if not.</summary>
    public static async Task ShouldBeHealthyAsync(this LedgerApiFactory factory)
    {
        var report = await factory.CheckInvariantsAsync();
        report.Healthy.ShouldBeTrue(string.Join("; ", report.Failures.Select(failure => $"{failure.Name}: {failure.Detail}")));
    }

    /// <summary>
    /// Runs SQL as the database owner, around the application on purpose (to prove the guards, or to
    /// simulate tampering). Disabling triggers needs ALTER TABLE rights, which the test user has.
    /// </summary>
    public static async Task ExecuteSqlAsync(this LedgerApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Cancel);
    }
}
