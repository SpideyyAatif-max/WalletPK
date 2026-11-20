using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Contracts.Fraud.V1;

namespace WalletPK.Fraud.IntegrationTests.Infrastructure;

/// <summary>A user as the Fraud service knows them: an account of some age, a device trusted at some time, a wallet number.</summary>
public sealed record TestUser(Guid Id, string WalletNumber, Guid DeviceId);

public static class FraudSteps
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static string NewNumber() => $"+92300{Random.Shared.Next(1_000_000, 10_000_000)}";

    /// <summary>A user registered <paramref name="accountDays"/> ago whose device was trusted <paramref name="deviceHours"/> ago.</summary>
    public static async Task<TestUser> UserAsync(this FraudFactory factory, double accountDays = 365, double deviceHours = 24 * 200)
    {
        var user = new TestUser(Guid.NewGuid(), NewNumber(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        await factory.Features.SetAccountAsync(user.Id, user.WalletNumber, now.AddDays(-accountDays));
        await factory.Features.SetDeviceAsync(user.Id, user.DeviceId, now.AddHours(-deviceHours), null);
        return user;
    }

    public static async Task<ScoreResponse> ScoreAsync(this FraudFactory factory, TestUser user, long amountPaisa, string? to = null, string type = "P2PTransfer", Guid? transactionId = null) =>
        await factory.Risk.ScoreAsync(
            new ScoreRequest
            {
                TransactionId = (transactionId ?? Guid.NewGuid()).ToString(),
                UserId = user.Id.ToString(),
                DeviceId = user.DeviceId.ToString(),
                TransactionType = type,
                AmountPaisa = amountPaisa,
                Counterparty = to ?? string.Empty,
            },
            cancellationToken: Cancel);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body) =>
        await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body ?? new { }, Cancel);

    public static async Task<JsonElement> WaitAsync(Func<Task<JsonElement>> read, Func<JsonElement, bool> done, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement value;
        do
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out waiting for {what}: {value}");
    }
}
