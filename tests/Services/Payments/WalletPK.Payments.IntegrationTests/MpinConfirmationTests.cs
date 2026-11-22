using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Payments.IntegrationTests.Infrastructure;

namespace WalletPK.Payments.IntegrationTests;

/// <summary>
/// Sending money needs the MPIN (proposal 12.1): a confirmation from Identity, for this user and device and
/// bound to this one payment, in X-Mpin-Confirmation. The same shared filter guards the Ledger's transfers.
/// </summary>
[Collection(PaymentsCollection.Name)]
public class MpinConfirmationTests(PaymentsFactory factory) : IAsyncLifetime
{
    private const string Header = "X-Mpin-Confirmation";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        factory.MpinConfirmation.Required = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        factory.MpinConfirmation.Required = false;
        factory.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string key, string? confirmation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Idempotency-Key", key);
        if (confirmation is not null)
        {
            request.Headers.Add(Header, confirmation);
        }

        return await client.SendAsync(request, Cancel);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    [Fact]
    public async Task A_transfer_without_a_valid_confirmation_for_this_payment_is_refused_and_can_then_be_sent_with_one()
    {
        var sender = Guid.NewGuid();
        var recipient = PaymentsFactory.PhoneOf(Guid.NewGuid());
        using var client = factory.ClientFor(sender);
        var key = Guid.NewGuid().ToString();
        var body = new { toWalletNumber = recipient, amountPaisa = 10_000 };

        string?[] refused =
        [
            null,
            "not-a-token",
            factory.CreateMpinConfirmation(sender, binding: Guid.NewGuid().ToString()),                          // another payment
            factory.CreateMpinConfirmation(Guid.NewGuid(), binding: key),                                         // another user
            factory.CreateMpinConfirmation(sender, binding: key, deviceId: "0193f1c4-0000-7000-8000-00000000ffff"), // another device
            factory.CreateMpinConfirmation(sender, binding: key, audience: "walletpk-api"),                       // an access token
            factory.CreateMpinConfirmation(sender, binding: key, lifetime: TimeSpan.FromMinutes(-2)),             // expired
        ];
        foreach (var confirmation in refused)
        {
            using var response = await PostAsync(client, "/api/payments/p2p-transfers", body, key, confirmation);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeAsync(response)).ShouldBe("auth.mpin_confirmation_required");
        }

        // A refusal is not remembered against the key: the same payment goes through once confirmed.
        using var sent = await PostAsync(client, "/api/payments/p2p-transfers", body, key, factory.CreateMpinConfirmation(sender, key));
        sent.StatusCode.ShouldBe(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Bank_transfers_and_paying_a_request_need_it_too()
    {
        var payer = Guid.NewGuid();
        using var client = factory.ClientFor(payer);
        var iban = TestIbans.NewActive("MUCB");
        var key = Guid.NewGuid().ToString();
        var transfer = new { toIban = iban, amountPaisa = 50_000, beneficiaryTitle = "Test Beneficiary" };

        using (var refused = await PostAsync(client, "/api/payments/bank-transfers", transfer, key, confirmation: null))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var sent = await PostAsync(client, "/api/payments/bank-transfers", transfer, key, factory.CreateMpinConfirmation(payer, key)))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync(Cancel));
        }

        // A money request is confirmed for "money-request:{id}" (it has no Idempotency-Key of its own).
        var requesterId = Guid.NewGuid();
        using var requester = factory.ClientFor(requesterId);
        using var created = await PostAsync(
            requester, "/api/payments/requests", new { fromWalletNumber = PaymentsFactory.PhoneOf(payer), amountPaisa = 20_000 }, Guid.NewGuid().ToString(), confirmation: null);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Cancel));
        var request = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("id").GetGuid();

        using (var refused = await PostAsync(client, $"/api/payments/requests/{request}/pay", null, Guid.NewGuid().ToString(), factory.CreateMpinConfirmation(payer, key)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var paid = await PostAsync(client, $"/api/payments/requests/{request}/pay", null, Guid.NewGuid().ToString(), factory.CreateMpinConfirmation(payer, $"money-request:{request}"));
        paid.StatusCode.ShouldBe(HttpStatusCode.Accepted, await paid.Content.ReadAsStringAsync(Cancel));
    }
}
