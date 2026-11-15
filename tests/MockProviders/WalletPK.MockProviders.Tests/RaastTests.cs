using System.Globalization;
using System.Net;
using System.Text;
using WalletPK.MockProviders.Banks;
using WalletPK.MockProviders.Tests.Infrastructure;
using WalletPK.Web.Security;
using static WalletPK.MockProviders.Tests.Infrastructure.TestKit;

namespace WalletPK.MockProviders.Tests;

/// <summary>M5.2: Raast and four banks, with signed confirmation callbacks.</summary>
[Collection(MockProvidersCollection.Name)]
public sealed class RaastTests(MockProvidersFactory factory) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await factory.ClearFaultsAsync();

    private static object Transfer(string transferId, string toIban, long amount = 250_000) =>
        new { transferId, amountPaisa = amount, toIban, senderName = "Ali Khan", purpose = "Rent" };

    private ReceivedCallback? CallbackFor(string transferId) =>
        factory.Callbacks.Received.LastOrDefault(callback => callback.Json.GetProperty("transferId").GetString() == transferId);

    [Fact]
    public async Task Alias_and_title_lookups_answer_the_same_every_time()
    {
        using var client = factory.Client();

        var alias = await client.GetJsonAsync("/raast/v1/aliases/0300-1234567");
        alias.GetProperty("alias").GetString().ShouldBe("+923001234567");
        var iban = alias.GetProperty("iban").GetString()!;
        Iban.Normalize(iban).ShouldBe(iban);
        (await client.GetJsonAsync("/raast/v1/aliases/923001234567")).GetProperty("iban").GetString().ShouldBe(iban);

        var account = await client.GetJsonAsync($"/raast/v1/accounts/{iban}");
        account.GetProperty("title").GetString().ShouldBe(alias.GetProperty("title").GetString());
        account.GetProperty("status").GetString().ShouldBe("Active");

        await client.GetJsonAsync("/raast/v1/aliases/03001234000", HttpStatusCode.NotFound);
        (await client.GetJsonAsync($"/raast/v1/accounts/{TestAccounts.Iban(lastFour: "0000")}")).GetProperty("status").GetString().ShouldBe("Closed");
        (await client.GetJsonAsync("/raast/v1/accounts/PK00HABB0000001234567890", HttpStatusCode.BadRequest)).GetProperty("code").GetString().ShouldBe("raast.invalid_iban");
        (await client.GetJsonAsync($"/raast/v1/accounts/{Iban.For("SCBL", "0000001123456702")}", HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("raast.unknown_bank");
    }

    [Fact]
    public async Task A_transfer_is_accepted_then_settled_and_confirmed_with_a_signed_callback()
    {
        using var client = factory.Client();
        var transferId = NewId();
        var iban = TestAccounts.Iban("MEZN");

        using var accepted = await client.PostJsonAsync("/raast/v1/transfers", Transfer(transferId, iban));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await accepted.JsonAsync();
        body.GetProperty("status").GetString().ShouldBe("Pending");
        body.GetProperty("toBank").GetString().ShouldBe("MEEZAN");
        var reference = body.GetProperty("raastReference").GetString();

        await EventuallyAsync(() => Task.FromResult(CallbackFor(transferId) is not null), "No callback arrived.");
        var callback = CallbackFor(transferId)!;
        callback.Url.ShouldBe(new Uri(MockProvidersFactory.CallbackUrl));
        callback.Json.GetProperty("event").GetString().ShouldBe("raast.transfer.completed");
        callback.Json.GetProperty("raastReference").GetString().ShouldBe(reference);
        callback.Json.GetProperty("amountPaisa").GetInt64().ShouldBe(250_000);

        // Signed like every webhook WalletPK accepts (the gateway checks it, M1.4).
        var timestamp = long.Parse(callback.Headers[WebhookSignature.TimestampHeader], CultureInfo.InvariantCulture);
        WebhookSignature.Verify(MockProvidersFactory.RaastSecret, timestamp, Encoding.UTF8.GetBytes(callback.Body), callback.Headers[WebhookSignature.SignatureHeader]).ShouldBeTrue();

        var status = await client.GetJsonAsync($"/raast/v1/transfers/{transferId}");
        status.GetProperty("status").GetString().ShouldBe("Completed");
        status.GetProperty("completedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
    }

    [Fact]
    public async Task Transfers_to_closed_or_blocked_accounts_are_rejected()
    {
        using var client = factory.Client();
        var closed = NewId();
        var blocked = NewId();

        (await client.PostJsonAsync("/raast/v1/transfers", Transfer(closed, TestAccounts.Iban("UNIL", "0000")))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostJsonAsync("/raast/v1/transfers", Transfer(blocked, TestAccounts.Iban("MUCB", "9999")))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await EventuallyAsync(() => Task.FromResult(CallbackFor(closed) is not null && CallbackFor(blocked) is not null), "No callbacks arrived.");
        CallbackFor(closed)!.Json.GetProperty("event").GetString().ShouldBe("raast.transfer.rejected");
        CallbackFor(closed)!.Json.GetProperty("reason").GetString().ShouldBe("account_closed");
        CallbackFor(blocked)!.Json.GetProperty("reason").GetString().ShouldBe("account_blocked");
    }

    [Fact]
    public async Task A_transfer_id_is_one_transfer_the_same_request_again_is_answered_the_same()
    {
        using var client = factory.Client();
        var transferId = NewId();
        var iban = TestAccounts.Iban();

        using var first = await client.PostJsonAsync("/raast/v1/transfers", Transfer(transferId, iban));
        using var again = await client.PostJsonAsync("/raast/v1/transfers", Transfer(transferId, iban));
        using var different = await client.PostJsonAsync("/raast/v1/transfers", Transfer(transferId, iban, amount: 999_900));

        (await again.JsonAsync()).GetProperty("raastReference").GetString().ShouldBe((await first.JsonAsync()).GetProperty("raastReference").GetString());
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await different.JsonAsync()).GetProperty("code").GetString().ShouldBe("provider.duplicate_id");
    }

    [Fact]
    public async Task Transfers_by_raast_id_go_to_the_linked_account()
    {
        using var client = factory.Client();
        var transferId = NewId();
        var alias = await client.GetJsonAsync("/raast/v1/aliases/03215550123");

        using var accepted = await client.PostJsonAsync("/raast/v1/transfers", new { transferId, amountPaisa = 10_000, toAlias = "0321 5550123" });

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await accepted.JsonAsync()).GetProperty("toIban").GetString().ShouldBe(alias.GetProperty("iban").GetString());
    }

    [Theory]
    [InlineData(99L, "raast.amount_out_of_range")]
    [InlineData(100_000_001L, "raast.amount_out_of_range")]
    public async Task Amounts_must_be_between_rs_1_and_rs_1_million(long paisa, string code)
    {
        using var client = factory.Client();
        using var response = await client.PostJsonAsync("/raast/v1/transfers", Transfer(NewId(), TestAccounts.Iban(), paisa));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task While_a_bank_is_down_new_transfers_are_refused_and_accepted_ones_wait_for_it()
    {
        using var client = factory.Client();
        var waiting = NewId();
        (await client.PostJsonAsync("/raast/v1/transfers", Transfer(waiting, TestAccounts.Iban("HABB")))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.SetFaultsAsync("bank:HBL", new { outage = true });

        using (var refused = await client.PostJsonAsync("/raast/v1/transfers", Transfer(NewId(), TestAccounts.Iban("HABB"))))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        // Other banks keep working, and the accepted HBL transfer is still in flight.
        (await client.PostJsonAsync("/raast/v1/transfers", Transfer(NewId(), TestAccounts.Iban("UNIL")))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(400, Cancel);
        (await client.GetJsonAsync($"/raast/v1/transfers/{waiting}")).GetProperty("status").GetString().ShouldBe("Pending");

        await factory.ClearFaultsAsync();
        await EventuallyAsync(
            async () => (await client.GetJsonAsync($"/raast/v1/transfers/{waiting}")).GetProperty("status").GetString() == "Completed",
            "The HBL transfer did not complete after the outage.");
    }

    [Fact]
    public async Task A_failed_callback_is_retried_until_it_is_delivered()
    {
        using var client = factory.Client();
        var transferId = NewId();
        factory.Callbacks.FailNext(transferId, 1);

        (await client.PostJsonAsync("/raast/v1/transfers", Transfer(transferId, TestAccounts.Iban()))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await EventuallyAsync(
            () => Task.FromResult(factory.Callbacks.Received.Count(callback => callback.Json.GetProperty("transferId").GetString() == transferId) >= 2),
            "The callback was not retried.");
        CallbackFor(transferId)!.Headers["X-Mock-Delivery-Attempt"].ShouldBe("2");

        using var admin = factory.Admin();
        await EventuallyAsync(
            async () => (await admin.GetJsonAsync("/admin/callbacks")).EnumerateArray()
                .Any(row => row.GetProperty("payload").GetString()!.Contains(transferId, StringComparison.Ordinal) && row.GetProperty("deliveredAt").ValueKind == System.Text.Json.JsonValueKind.String),
            "The callback is not marked delivered.");
    }

    [Fact]
    public async Task Status_of_a_transfer_raast_never_got_is_404()
    {
        using var client = factory.Client();
        (await client.GetJsonAsync($"/raast/v1/transfers/{NewId()}", HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("raast.transfer_not_found");
    }
}
