using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Payments.IntegrationTests.Infrastructure;
using WalletPK.Web.Security;

namespace WalletPK.Payments.IntegrationTests;

/// <summary>M6.4–M6.7: Raast transfers end to end, including bank outages, lost callbacks and unknown outcomes.</summary>
[Collection(PaymentsCollection.Name)]
public class BankTransferTests(PaymentsFactory factory) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    // Faults and the clock are shared: every test leaves them as it found them.
    public async ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        factory.Callbacks.Enabled = true;
        using var admin = factory.Providers.Admin();
        using var _ = await admin.DeleteAsync(new Uri("/admin/faults", UriKind.Relative), CancellationToken.None);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string iban, long amountPaisa, string? key = null, string? title = "Test Beneficiary")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/bank-transfers")
        {
            Content = JsonContent.Create(new { toIban = iban, amountPaisa, beneficiaryTitle = title, note = "Rent" }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Cancel);
    }

    private static async Task<Guid> StartAsync(HttpClient client, string iban, long amountPaisa = 250_000)
    {
        using var response = await SendAsync(client, iban, amountPaisa);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/payments/bank-transfers/{id}", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonAsync(response);
    }

    /// <summary>Polls the tracker until the transfer has <paramref name="step"/> (a Saga state).</summary>
    private static async Task<JsonElement> WaitForStepAsync(HttpClient client, Guid id, string step, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement transfer;
        do
        {
            transfer = await GetAsync(client, id);
            if (transfer.GetProperty("step").GetString() == step)
            {
                return transfer;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Transfer {id} did not reach {step}; it is {transfer.GetProperty("step")}: {transfer}");
    }

    private static string[] Steps(JsonElement transfer) =>
        [.. transfer.GetProperty("timeline").EnumerateArray().Select(step => step.GetProperty("state").GetString()!)];

    private async Task SetFaultAsync(string provider, object settings)
    {
        using var admin = factory.Providers.Admin();
        using var response = await admin.PutAsJsonAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), settings, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task<int> RaastTransfersAsync(Guid id)
    {
        using var client = factory.Providers.Client();
        using var response = await client.GetAsync(new Uri($"/raast/v1/transfers/{id}", UriKind.Relative), Cancel);
        return response.StatusCode == HttpStatusCode.OK ? 1 : 0;
    }

    [Fact]
    public async Task A_transfer_is_reserved_sent_confirmed_by_the_bank_and_settled()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        var iban = TestIbans.NewActive("MUCB");

        // The customer looks the account up first.
        using (var lookup = await client.PostAsJsonAsync(new Uri("/api/payments/beneficiaries/lookup", UriKind.Relative), new { iban }, Cancel))
        {
            lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
            var beneficiary = await JsonAsync(lookup);
            beneficiary.GetProperty("bank").GetString().ShouldBe("MCB");
            beneficiary.GetProperty("accountStatus").GetString().ShouldBe("Active");
            beneficiary.GetProperty("canReceive").GetBoolean().ShouldBeTrue();
            beneficiary.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        using var response = await SendAsync(client, iban, 250_000);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull();
        var accepted = await JsonAsync(response);
        var id = accepted.GetProperty("id").GetGuid();
        accepted.GetProperty("status").GetString().ShouldBe("Processing");
        accepted.GetProperty("toIban").GetString().ShouldBe($"{iban[..4]} {iban[4..8]} **** **** **** {iban[^4..]}");

        var done = await WaitForStepAsync(client, id, "Completed");
        done.GetProperty("status").GetString().ShouldBe("Completed");
        done.GetProperty("raastReference").GetString().ShouldStartWith("RAAST");
        Steps(done).ShouldBe(["Reserving", "Sending", "AwaitingBank", "Settling", "Completed"]);

        // The Ledger was asked to reserve, then to settle into the bank float (Raast is free: no fee).
        var commands = factory.Ledger.CommandsFor(id);
        var place = commands.OfType<PlaceHold>().First();
        place.ShouldSatisfyAllConditions(
            () => place.UserId.ShouldBe(user),
            () => place.AmountPaisa.ShouldBe(250_000),
            () => place.Purpose.ShouldBe("BankTransfer"));
        var capture = commands.OfType<CaptureHold>().First();
        capture.TransactionId.ShouldBe(id);
        capture.Credits.ShouldBe([new HoldCredit("BANK_FLOAT", 250_000)]);
        commands.OfType<ReleaseHold>().ShouldBeEmpty();
        (await RaastTransfersAsync(id)).ShouldBe(1);

        // Status events for the customer's apps.
        var events = factory.ReadUntil(KafkaTopics.PaymentsEvents, read =>
            read.Any(e => e.Payload.GetProperty("transferId").GetGuid() == id && e.Payload.GetProperty("status").GetString() == "Completed"));
        events.Where(e => e.Payload.GetProperty("transferId").GetGuid() == id).Select(e => e.Payload.GetProperty("status").GetString())
            .ShouldBe(["Processing", "Completed"]);
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_transfer_and_a_different_body_is_refused()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var iban = TestIbans.NewActive("MEZN");
        var key = Guid.NewGuid().ToString();

        using var first = await SendAsync(client, iban, 100_000, key);
        using var again = await SendAsync(client, iban, 100_000, key);
        using var different = await SendAsync(client, iban, 999_900, key);
        using var noKey = await client.PostAsJsonAsync(new Uri("/api/payments/bank-transfers", UriKind.Relative), new { toIban = iban, amountPaisa = 100_000 }, Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe((await JsonAsync(first)).GetProperty("id").GetGuid());
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_transfer_the_wallet_cannot_cover_fails_without_calling_the_bank()
    {
        var user = Guid.NewGuid();
        factory.Ledger.Refuse(user, "wallet.insufficient_balance", "Your available balance is Rs. 120.");
        using var client = factory.ClientFor(user);

        var id = await StartAsync(client, TestIbans.NewActive("HABB"));
        var failed = await WaitForStepAsync(client, id, "Failed");

        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");
        Steps(failed).ShouldBe(["Reserving", "Failed"]);
        (await RaastTransfersAsync(id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_transfer_to_a_closed_account_is_refused_by_the_bank_and_the_money_returned()
    {
        using var client = factory.ClientFor(Guid.NewGuid());

        var id = await StartAsync(client, TestIbans.Closed("UNIL"));
        var failed = await WaitForStepAsync(client, id, "Failed");

        failed.GetProperty("failureCode").GetString().ShouldBe("bank.account_closed");
        failed.GetProperty("failureReason").GetString()!.ShouldContain("the account is closed");
        Steps(failed).ShouldBe(["Reserving", "Sending", "AwaitingBank", "Returning", "Failed"]);
        factory.Ledger.CommandsFor(id).OfType<ReleaseHold>().ShouldNotBeEmpty();
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().ShouldBeEmpty();
        factory.Ledger.HoldState(id).ShouldBe("Released");
    }

    [Fact]
    public async Task While_a_bank_is_down_its_transfers_are_queued_its_circuit_opens_and_other_banks_carry_on()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        await SetFaultAsync("bank:HBL", new { outage = true });

        // Enough calls to HBL fail for its circuit to open.
        var queued = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            queued.Add(await StartAsync(client, TestIbans.NewActive("HABB"), 50_000 + i));
        }

        foreach (var id in queued)
        {
            var transfer = await WaitForStepAsync(client, id, "Queued");
            transfer.GetProperty("status").GetString().ShouldBe("Queued");
        }

        using (var banks = await client.GetAsync(new Uri("/api/payments/banks", UriKind.Relative), Cancel))
        {
            var hbl = (await JsonAsync(banks)).EnumerateArray().Single(bank => bank.GetProperty("code").GetString() == "HBL");
            hbl.GetProperty("status").GetString().ShouldBeOneOf("Down", "Degraded");
        }

        // MCB is unaffected: its own pipeline, its own circuit.
        var other = await StartAsync(client, TestIbans.NewActive("MUCB"));
        (await WaitForStepAsync(client, other, "Completed")).GetProperty("status").GetString().ShouldBe("Completed");

        // HBL comes back: the queued transfers go through by themselves.
        using (var admin = factory.Providers.Admin())
        using (await admin.DeleteAsync(new Uri("/admin/faults/bank:HBL", UriKind.Relative), Cancel))
        {
        }

        foreach (var id in queued)
        {
            var done = await WaitForStepAsync(client, id, "Completed", seconds: 60);
            Steps(done).ShouldBe(["Reserving", "Sending", "Queued", "AwaitingBank", "Settling", "Completed"]);
            (await RaastTransfersAsync(id)).ShouldBe(1);
        }

        var events = factory.ReadUntil(KafkaTopics.PaymentsEvents, read =>
            read.Count(e => e.Payload.GetProperty("transferId").GetGuid() == queued[0]) >= 4);
        events.Where(e => e.Payload.GetProperty("transferId").GetGuid() == queued[0]).Select(e => e.Payload.GetProperty("status").GetString())
            .ShouldBe(["Processing", "Queued", "Processing", "Completed"]);

        using (var banks = await client.GetAsync(new Uri("/api/payments/banks", UriKind.Relative), Cancel))
        {
            var hbl = (await JsonAsync(banks)).EnumerateArray().Single(bank => bank.GetProperty("code").GetString() == "HBL");
            hbl.GetProperty("status").GetString().ShouldBe("Available");
        }
    }

    [Fact]
    public async Task A_queued_transfer_is_given_up_after_the_queue_limit_and_the_money_returned()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        await SetFaultAsync("bank:MEEZAN", new { outage = true });

        var id = await StartAsync(client, TestIbans.NewActive("MEZN"));
        await WaitForStepAsync(client, id, "Queued");

        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        var failed = await WaitForStepAsync(client, id, "Failed");

        failed.GetProperty("failureCode").GetString().ShouldBe("payments.bank_unavailable");
        Steps(failed).ShouldBe(["Reserving", "Sending", "Queued", "Returning", "Failed"]);
        factory.Ledger.HoldState(id).ShouldBe("Released");
        (await RaastTransfersAsync(id)).ShouldBe(0);
    }

    [Fact]
    public async Task Without_the_banks_callback_the_transfer_completes_by_asking_raast()
    {
        factory.Callbacks.Enabled = false;
        using var client = factory.ClientFor(Guid.NewGuid());

        var id = await StartAsync(client, TestIbans.NewActive("UNIL"));
        var done = await WaitForStepAsync(client, id, "Completed");

        done.GetProperty("status").GetString().ShouldBe("Completed");
        Steps(done).ShouldBe(["Reserving", "Sending", "AwaitingBank", "Settling", "Completed"]);
        done.GetProperty("timeline").EnumerateArray().Single(step => step.GetProperty("state").GetString() == "Settling")
            .GetProperty("detail").GetString()!.ShouldContain("credited the beneficiary");
    }

    [Fact]
    public async Task When_the_send_times_out_but_raast_took_it_the_transfer_is_verified_not_sent_twice()
    {
        // The bank takes the transfer, but the answer never arrives (every try), and neither does the callback.
        await SetFaultAsync("bank:MCB", new { acceptButTimeoutRate = 1.0, timeoutMs = 300 });
        factory.Callbacks.Enabled = false;
        using var client = factory.ClientFor(Guid.NewGuid());

        var id = await StartAsync(client, TestIbans.NewActive("MUCB"));
        await WaitForStepAsync(client, id, "Verifying");
        using (var admin = factory.Providers.Admin())
        using (await admin.DeleteAsync(new Uri("/admin/faults/bank:MCB", UriKind.Relative), Cancel))
        {
        }

        var done = await WaitForStepAsync(client, id, "Completed");
        Steps(done).ShouldContain("Verifying");
        Steps(done)[^2..].ShouldBe(["Settling", "Completed"]);
        (await RaastTransfersAsync(id)).ShouldBe(1);
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().Select(capture => capture.Credits.Sum(credit => credit.AmountPaisa)).Distinct().ShouldBe([250_000L]);
    }

    [Fact]
    public async Task The_banks_confirmation_can_overtake_a_send_that_is_still_waiting_for_an_answer()
    {
        // The send hangs (and is retried) while the bank has already settled it and called back.
        await SetFaultAsync("bank:MCB", new { acceptButTimeoutRate = 1.0, timeoutMs = 1500 });
        using var client = factory.ClientFor(Guid.NewGuid());

        var id = await StartAsync(client, TestIbans.NewActive("MUCB"));
        var done = await WaitForStepAsync(client, id, "Completed");

        // The send's own (late) outcome was thrown away: the callback had moved the transfer on.
        Steps(done).ShouldBe(["Reserving", "Sending", "Settling", "Completed"]);
        (await RaastTransfersAsync(id)).ShouldBe(1);
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_lost_ledger_reply_is_asked_for_again()
    {
        var user = Guid.NewGuid();
        factory.Ledger.IgnoreNext(user, 1);
        using var client = factory.ClientFor(user);

        var id = await StartAsync(client, TestIbans.NewActive("HABB"));
        await WaitForStepAsync(client, id, "Completed");

        factory.Ledger.CommandsFor(id).OfType<PlaceHold>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Raast_callbacks_must_be_signed()
    {
        using var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { transferId = Guid.NewGuid(), raastReference = "RAAST1", status = "Completed" }));
        var now = factory.Clock.GetUtcNow().ToUnixTimeSeconds();

        async Task<HttpStatusCode> PostAsync(long timestamp, string? signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/raast/transfer-status") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            if (signature is not null)
            {
                request.Headers.Add(WebhookSignature.SignatureHeader, signature);
            }

            using var response = await client.SendAsync(request, Cancel);
            return response.StatusCode;
        }

        (await PostAsync(now, null)).ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(now, WebhookSignature.Compute("wrong-secret", now, body))).ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(now - 3600, WebhookSignature.Compute(PaymentsFactory.RaastSecret, now - 3600, body))).ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(now, WebhookSignature.Compute(PaymentsFactory.RaastSecret, now, body))).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfers_are_private_to_their_sender_and_listed_newest_first()
    {
        var owner = Guid.NewGuid();
        using var client = factory.ClientFor(owner);
        using var stranger = factory.ClientFor(Guid.NewGuid());

        var first = await StartAsync(client, TestIbans.NewActive("UNIL"), 10_000);
        var second = await StartAsync(client, TestIbans.NewActive("UNIL"), 20_000);

        using (var response = await stranger.GetAsync(new Uri($"/api/payments/bank-transfers/{first}", UriKind.Relative), Cancel))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var list = await client.GetAsync(new Uri("/api/payments/bank-transfers?limit=10", UriKind.Relative), Cancel);
        var transfers = (await JsonAsync(list)).EnumerateArray().Select(transfer => transfer.GetProperty("id").GetGuid()).ToList();
        transfers.ShouldBe([second, first]);

        using var strangers = await stranger.GetAsync(new Uri("/api/payments/bank-transfers", UriKind.Relative), Cancel);
        (await JsonAsync(strangers)).GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("PK00HABB0000001234567890", 10_000, "payments.invalid_iban")]
    [InlineData("SCBL", 10_000, "payments.invalid_iban")]
    [InlineData(null, 10_000, "payments.invalid_iban")]
    [InlineData("valid-HABB", 50, "payments.amount_out_of_range")]
    [InlineData("valid-HABB", 100_000_001, "payments.amount_out_of_range")]
    [InlineData("valid-SCBL", 10_000, "payments.bank_not_supported")]
    public async Task Invalid_transfers_are_refused(string? iban, long amountPaisa, string code)
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var target = iban switch
        {
            "valid-HABB" => TestIbans.NewActive("HABB"),
            "valid-SCBL" => TestIbans.NewActive("SCBL"),
            _ => iban,
        };

        using var response = await SendAsync(client, target!, amountPaisa);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Customer_endpoints_need_a_token()
    {
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(new Uri("/api/payments/banks", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
