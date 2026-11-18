using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Bills.Application.Saved;
using WalletPK.Bills.Infrastructure.Persistence;
using WalletPK.Bills.Infrastructure.Reminders;
using WalletPK.Bills.IntegrationTests.Infrastructure;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;

namespace WalletPK.Bills.IntegrationTests;

/// <summary>M7: bill inquiry and payment end to end, including outages, unknown outcomes, the dead-letter queue and reminders.</summary>
[Collection(BillsCollection.Name)]
public class BillPaymentTests(BillsFactory factory) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        using var admin = factory.Providers.Admin();
        using var _ = await admin.DeleteAsync(new Uri("/admin/faults", UriKind.Relative), CancellationToken.None);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<JsonElement> InquireAsync(HttpClient client, string biller, string consumer)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/bills/inquiry", UriKind.Relative), new { biller, consumerNumber = consumer }, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    private static async Task<HttpResponseMessage> PayAsync(HttpClient client, string biller, string consumer, long amount, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bills/payments")
        {
            Content = JsonContent.Create(new { biller, consumerNumber = consumer, amountPaisa = amount }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Cancel);
    }

    /// <summary>Inquires, then pays the payable amount; returns the payment ID.</summary>
    private static async Task<Guid> PayBillAsync(HttpClient client, string biller, string consumer)
    {
        var bill = await InquireAsync(client, biller, consumer);
        using var response = await PayAsync(client, biller, consumer, bill.GetProperty("payablePaisa").GetInt64());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> WaitForStepAsync(HttpClient client, Guid id, string step, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement payment;
        do
        {
            using var response = await client.GetAsync(new Uri($"/api/bills/payments/{id}", UriKind.Relative), Cancel);
            payment = await JsonAsync(response);
            if (payment.GetProperty("step").GetString() == step)
            {
                return payment;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Payment {id} did not reach {step}: {payment}");
    }

    private static string[] Steps(JsonElement payment) =>
        [.. payment.GetProperty("timeline").EnumerateArray().Select(step => step.GetProperty("state").GetString()!)];

    private async Task SetFaultAsync(string provider, object settings)
    {
        using var admin = factory.Providers.Admin();
        using var response = await admin.PutAsJsonAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), settings, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
    }

    private async Task ClearFaultAsync(string provider)
    {
        using var admin = factory.Providers.Admin();
        using var _ = await admin.DeleteAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), Cancel);
    }

    /// <summary>Whether the biller holds payment <paramref name="id"/> (its status query).</summary>
    private async Task<bool> BillerHasAsync(string biller, Guid id)
    {
        using var client = factory.Providers.Client();
        using var response = await client.GetAsync(new Uri($"/billers/{biller}/payments/{id}", UriKind.Relative), Cancel);
        return response.StatusCode == HttpStatusCode.OK;
    }

    // ---- M7.1, M7.2 ----

    [Fact]
    public async Task The_catalog_lists_the_billers_and_their_consumer_number_rules()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        using var response = await client.GetAsync(new Uri("/api/bills/billers", UriKind.Relative), Cancel);

        var billers = (await JsonAsync(response)).EnumerateArray().ToList();
        billers.Select(biller => biller.GetProperty("code").GetString()).ShouldBe(["KE", "LESCO", "SNGPL", "PTCL"]);
        billers.Single(biller => biller.GetProperty("code").GetString() == "KE").GetProperty("consumerNumberLength").GetInt32().ShouldBe(13);
        billers.ShouldAllBe(biller => biller.GetProperty("status").GetString() == "Available");
    }

    [Fact]
    public async Task An_inquiry_shows_this_months_bill()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var consumer = Consumers.New(13);

        var bill = await InquireAsync(client, "KE", consumer);
        bill.GetProperty("customerName").GetString().ShouldNotBeNullOrWhiteSpace();
        bill.GetProperty("status").GetString().ShouldBe("Unpaid");
        bill.GetProperty("payablePaisa").GetInt64().ShouldBeGreaterThan(0);

        (await InquireAsync(client, "KE", Consumers.AlreadyPaid(13))).GetProperty("status").GetString().ShouldBe("Paid");

        using var unknown = await client.PostAsJsonAsync(new Uri("/api/bills/inquiry", UriKind.Relative), new { biller = "KE", consumerNumber = Consumers.Unknown(13) }, Cancel);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Checked here, before any biller is called.
        using var invalid = await client.PostAsJsonAsync(new Uri("/api/bills/inquiry", UriKind.Relative), new { biller = "KE", consumerNumber = "12345" }, Cancel);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(invalid)).GetProperty("code").GetString().ShouldBe("bills.invalid_consumer_number");
    }

    // ---- M7.3 ----

    [Fact]
    public async Task A_bill_is_reserved_paid_and_settled_to_the_biller_settlement_account()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        var consumer = Consumers.New(14);

        var id = await PayBillAsync(client, "LESCO", consumer);
        var done = await WaitForStepAsync(client, id, "Completed");

        done.GetProperty("status").GetString().ShouldBe("Completed");
        done.GetProperty("billerReference").GetString().ShouldNotBeNullOrWhiteSpace();
        done.GetProperty("billingMonth").GetString()!.ShouldMatch(@"^\d{4}-\d{2}$");
        Steps(done).ShouldBe(["Reserving", "Paying", "Settling", "Completed"]);

        var place = factory.Ledger.CommandsFor(id).OfType<PlaceHold>().First();
        place.Purpose.ShouldBe("BillPayment");
        place.UserId.ShouldBe(user);
        var capture = factory.Ledger.CommandsFor(id).OfType<CaptureHold>().Single();
        capture.Credits.ShouldBe([new HoldCredit("BILLER_SETTLEMENT", place.AmountPaisa)]);
        (await BillerHasAsync("LESCO", id)).ShouldBeTrue();

        // Now the bill shows paid, and the payment is in my list.
        (await InquireAsync(client, "LESCO", consumer)).GetProperty("status").GetString().ShouldBe("Paid");
        using var list = await client.GetAsync(new Uri("/api/bills/payments", UriKind.Relative), Cancel);
        (await JsonAsync(list)).EnumerateArray().Select(payment => payment.GetProperty("id").GetGuid()).ShouldBe([id]);

        var events = factory.ReadUntil(KafkaTopics.BillsEvents, read =>
            read.Any(e => e.EventType == "bills.payment_changed" && e.Payload.GetProperty("paymentId").GetGuid() == id && e.Payload.GetProperty("status").GetString() == "Completed"));
        events.Where(e => e.EventType == "bills.payment_changed" && e.Payload.GetProperty("paymentId").GetGuid() == id).Select(e => e.Payload.GetProperty("status").GetString())
            .ShouldBe(["Processing", "Completed"]);
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_payment_and_a_different_body_is_refused()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var consumer = Consumers.New(11);
        var bill = await InquireAsync(client, "SNGPL", consumer);
        var amount = bill.GetProperty("payablePaisa").GetInt64();
        var key = Guid.NewGuid().ToString();

        using var first = await PayAsync(client, "SNGPL", consumer, amount, key);
        using var again = await PayAsync(client, "SNGPL", consumer, amount, key);
        using var different = await PayAsync(client, "SNGPL", consumer, amount + 100, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe((await JsonAsync(first)).GetProperty("id").GetGuid());
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("disconnected", "biller.connection_disconnected")]
    [InlineData("wrong-amount", "biller.amount_mismatch")]
    public async Task A_bill_the_biller_refuses_is_not_paid_and_the_money_returned(string kind, string code)
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var consumer = kind == "disconnected" ? Consumers.Disconnected(11) : Consumers.New(11);
        var bill = await InquireAsync(client, "PTCL", consumer);
        var amount = bill.GetProperty("payablePaisa").GetInt64() + (kind == "wrong-amount" ? 100 : 0);

        using var response = await PayAsync(client, "PTCL", consumer, amount);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var failed = await WaitForStepAsync(client, id, "Failed");

        failed.GetProperty("failureCode").GetString().ShouldBe(code);
        Steps(failed).ShouldBe(["Reserving", "Paying", "Returning", "Failed"]);
        factory.Ledger.HoldState(id).ShouldBe("Released");
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bill_the_wallet_cannot_cover_fails_without_calling_the_biller()
    {
        var user = Guid.NewGuid();
        factory.Ledger.Refuse(user, "wallet.insufficient_balance", "Your available balance is Rs. 40.");
        using var client = factory.ClientFor(user);

        var id = await PayBillAsync(client, "KE", Consumers.New(13));
        var failed = await WaitForStepAsync(client, id, "Failed");

        failed.GetProperty("failureCode").GetString().ShouldBe("wallet.insufficient_balance");
        Steps(failed).ShouldBe(["Reserving", "Failed"]);
        (await BillerHasAsync("KE", id)).ShouldBeFalse();
    }

    [Fact]
    public async Task While_a_biller_is_down_the_payment_is_retried_and_goes_through_when_it_is_back()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var consumer = Consumers.New(11);
        var bill = await InquireAsync(client, "SNGPL", consumer);
        await SetFaultAsync("biller:SNGPL", new { outage = true });

        using var response = await PayAsync(client, "SNGPL", consumer, bill.GetProperty("payablePaisa").GetInt64());
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await WaitForStepAsync(client, id, "Retrying");

        await ClearFaultAsync("biller:SNGPL");
        var done = await WaitForStepAsync(client, id, "Completed");

        Steps(done).ShouldBe(["Reserving", "Paying", "Retrying", "Settling", "Completed"]);
        (await BillerHasAsync("SNGPL", id)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_timed_out_payment_is_checked_with_the_biller_before_anything_is_retried()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        var consumer = Consumers.New(11);
        var bill = await InquireAsync(client, "PTCL", consumer);

        // The biller takes the payment but no answer comes back (nor to status queries, for now).
        await SetFaultAsync("biller:PTCL", new { acceptButTimeoutRate = 1.0, timeoutMs = 200 });
        using var response = await PayAsync(client, "PTCL", consumer, bill.GetProperty("payablePaisa").GetInt64());
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await WaitForStepAsync(client, id, "Verifying");

        await ClearFaultAsync("biller:PTCL");
        var done = await WaitForStepAsync(client, id, "Completed");

        Steps(done).ShouldBe(["Reserving", "Paying", "Verifying", "Settling", "Completed"]);
        done.GetProperty("timeline").EnumerateArray().Single(step => step.GetProperty("state").GetString() == "Settling")
            .GetProperty("detail").GetString()!.ShouldContain("confirms the payment arrived");
    }

    // ---- M7.4, M7.5 ----

    [Fact]
    public async Task An_unconfirmed_payment_waits_in_pending_verification_then_is_dead_lettered_and_resolved_by_operations()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        using var ops = factory.ClientFor(Guid.NewGuid(), role: "ops");
        var consumer = Consumers.New(13);
        var bill = await InquireAsync(client, "KE", consumer);

        await SetFaultAsync("biller:KE", new { acceptButTimeoutRate = 1.0, timeoutMs = 200 });
        using var response = await PayAsync(client, "KE", consumer, bill.GetProperty("payablePaisa").GetInt64());
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await WaitForStepAsync(client, id, "Verifying");

        // Ten minutes without an answer: pending verification; the money stays reserved.
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var pending = await WaitForStepAsync(client, id, "PendingVerification");
        pending.GetProperty("status").GetString().ShouldBe("PendingVerification");
        factory.Ledger.HoldState(id).ShouldBe("Active");

        // Two days: dead-lettered, on bills.dlq, and in the operations queue.
        factory.Clock.Advance(TimeSpan.FromHours(49));
        var dead = await WaitForStepAsync(client, id, "DeadLettered");
        dead.GetProperty("status").GetString().ShouldBe("UnderReview");
        factory.ReadUntil(KafkaTopics.BillsDeadLetter, read => read.Any(e => e.Payload.GetProperty("paymentId").GetGuid() == id));

        using (var queue = await ops.GetAsync(new Uri("/api/bills/ops/dead-letters", UriKind.Relative), Cancel))
        {
            queue.StatusCode.ShouldBe(HttpStatusCode.OK);
            var item = (await JsonAsync(queue)).EnumerateArray().Single(entry => entry.GetProperty("id").GetGuid() == id);
            item.GetProperty("step").GetString().ShouldBe("DeadLettered");
            item.GetProperty("userId").GetGuid().ShouldBe(user);
        }

        // The biller is answering again: an operator replays it, and the status query finds the payment.
        await ClearFaultAsync("biller:KE");
        using (var replay = await ops.PostAsync(new Uri($"/api/bills/ops/payments/{id}/replay", UriKind.Relative), null, Cancel))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync(Cancel));
        }

        var done = await WaitForStepAsync(client, id, "Completed");
        Steps(done).ShouldBe(["Reserving", "Paying", "Verifying", "PendingVerification", "DeadLettered", "Verifying", "Settling", "Completed"]);
        factory.Ledger.CommandsFor(id).OfType<CaptureHold>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_operator_can_refund_a_payment_the_biller_never_confirmed()
    {
        using var client = factory.ClientFor(Guid.NewGuid());
        using var ops = factory.ClientFor(Guid.NewGuid(), role: "ops");
        var consumer = Consumers.New(14);
        var bill = await InquireAsync(client, "LESCO", consumer);

        await SetFaultAsync("biller:LESCO", new { timeoutRate = 1.0, timeoutMs = 2000 });
        using var response = await PayAsync(client, "LESCO", consumer, bill.GetProperty("payablePaisa").GetInt64());
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await WaitForStepAsync(client, id, "Verifying");
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        await WaitForStepAsync(client, id, "PendingVerification");

        // Customers cannot use the operations endpoints.
        using (var forbidden = await client.PostAsJsonAsync(new Uri($"/api/bills/ops/payments/{id}/refund", UriKind.Relative), new { reason = "please" }, Cancel))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var refund = await ops.PostAsJsonAsync(new Uri($"/api/bills/ops/payments/{id}/refund", UriKind.Relative), new { reason = "Not in LESCO's settlement file" }, Cancel))
        {
            refund.StatusCode.ShouldBe(HttpStatusCode.OK, await refund.Content.ReadAsStringAsync(Cancel));
        }

        var failed = await WaitForStepAsync(client, id, "Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("bills.refunded_by_operator");
        factory.Ledger.HoldState(id).ShouldBe("Released");

        // A finished payment cannot be resolved again.
        using var again = await ops.PostAsync(new Uri($"/api/bills/ops/payments/{id}/replay", UriKind.Relative), null, Cancel);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- M7.6 ----

    [Fact]
    public async Task Saved_billers_remember_the_last_bill_and_are_reminded_once_per_bill()
    {
        var user = Guid.NewGuid();
        using var client = factory.ClientFor(user);
        var consumer = Consumers.New(13);

        using (var save = await client.PostAsJsonAsync(new Uri("/api/bills/saved", UriKind.Relative), new { biller = "KE", consumerNumber = consumer, nickname = "Home" }, Cancel))
        {
            save.StatusCode.ShouldBe(HttpStatusCode.OK, await save.Content.ReadAsStringAsync(Cancel));
            var saved = await JsonAsync(save);
            saved.GetProperty("customerName").GetString().ShouldNotBeNullOrWhiteSpace();
            saved.GetProperty("lastStatus").GetString().ShouldBe("Unpaid");
        }

        using (var again = await client.PostAsJsonAsync(new Uri("/api/bills/saved", UriKind.Relative), new { biller = "KE", consumerNumber = consumer, nickname = "Home (DHA)" }, Cancel))
        {
            (await JsonAsync(again)).GetProperty("nickname").GetString().ShouldBe("Home (DHA)");
        }

        using (var unknown = await client.PostAsJsonAsync(new Uri("/api/bills/saved", UriKind.Relative), new { biller = "KE", consumerNumber = Consumers.Unknown(13) }, Cancel))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var list = await client.GetAsync(new Uri("/api/bills/saved", UriKind.Relative), Cancel);
        var entry = (await JsonAsync(list)).EnumerateArray().Single();
        var savedId = entry.GetProperty("id").GetGuid();

        // The reminder job: an unpaid bill due soon (tests remind up to 20 days ahead) → one reminder.
        var reminders = factory.Services.GetRequiredService<BillReminderService>();
        await reminders.RunOnceAsync(Cancel);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillsDbContext>();
            await db.Set<SavedBiller>().Where(saved => saved.Id == savedId).ExecuteUpdateAsync(set => set.SetProperty(saved => saved.NextCheckAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Cancel);
        }

        await reminders.RunOnceAsync(Cancel);
        var events = factory.ReadUntil(KafkaTopics.BillsEvents, read => read.Any(e => e.EventType == "bills.bill_due_reminder" && e.Payload.GetProperty("savedBillerId").GetGuid() == savedId));
        await Task.Delay(500, Cancel);
        events = factory.ReadUntil(KafkaTopics.BillsEvents, read => read.Count >= events.Count, TimeSpan.FromSeconds(5));
        var mine = events.Where(e => e.EventType == "bills.bill_due_reminder" && e.Payload.GetProperty("savedBillerId").GetGuid() == savedId).ToList();
        mine.Count.ShouldBe(1);
        mine[0].Payload.GetProperty("nickname").GetString().ShouldBe("Home (DHA)");
        mine[0].Payload.GetProperty("userId").GetGuid().ShouldBe(user);

        using (var delete = await client.DeleteAsync(new Uri($"/api/bills/saved/{savedId}", UriKind.Relative), Cancel))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var empty = await client.GetAsync(new Uri("/api/bills/saved", UriKind.Relative), Cancel);
        (await JsonAsync(empty)).GetArrayLength().ShouldBe(0);
    }
}
