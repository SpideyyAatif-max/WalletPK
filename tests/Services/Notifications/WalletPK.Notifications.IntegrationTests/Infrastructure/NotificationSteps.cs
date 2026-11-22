using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;

namespace WalletPK.Notifications.IntegrationTests.Infrastructure;

/// <summary>A customer as the other services know them: an ID, a mobile number and a wallet.</summary>
public sealed record Customer(Guid Id, string Phone, Guid WalletId);

public static class NotificationSteps
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static string NewPhone() => string.Create(CultureInfo.InvariantCulture, $"+92300{Random.Shared.Next(1_000_000, 9_999_999)}");

    /// <summary>Registers a customer (identity.user_registered) and waits for their welcome notification.</summary>
    public static async Task<Customer> CustomerAsync(this NotificationsFactory factory)
    {
        var customer = new Customer(Guid.NewGuid(), NewPhone(), Guid.NewGuid());
        await factory.PublishAsync(new UserRegistered(customer.Id, customer.Phone, "L1", DateTimeOffset.UtcNow));
        using var client = factory.ClientFor(customer.Id);
        await WaitAsync(() => client.PageAsync(), page => page.GetProperty("items").GetArrayLength() > 0, "welcome notification");
        return customer;
    }

    /// <summary>A wallet entry as the Ledger publishes it.</summary>
    public static WalletEntryPosted Entry(
        Customer owner, long sequence, string direction, long amount, long balance, string journalType = "P2PTransfer", string? counterparty = null,
        DateTimeOffset? postedAt = null, string walletType = "CustomerWallet", string description = "Transfer") =>
        new(owner.WalletId, owner.Id, owner.Phone, walletType, sequence, Guid.NewGuid(), journalType, description, direction, amount, balance, counterparty, postedAt ?? DateTimeOffset.UtcNow);

    public static async Task<JsonElement> PageAsync(this HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(new Uri($"/api/notifications{query}", UriKind.Relative), Cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);

    /// <summary>The newest notification of a template.</summary>
    public static async Task<JsonElement> NotificationAsync(this HttpClient client, string template, int seconds = 20) =>
        (await WaitAsync(
            () => client.PageAsync(),
            page => page.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("template").GetString() == template),
            $"a {template} notification",
            seconds)).GetProperty("items").EnumerateArray().First(item => item.GetProperty("template").GetString() == template);

    public static async Task<T> WaitAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 20)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {seconds} s for {what}.");
            }

            await Task.Delay(100, Cancel);
        }
    }
}
