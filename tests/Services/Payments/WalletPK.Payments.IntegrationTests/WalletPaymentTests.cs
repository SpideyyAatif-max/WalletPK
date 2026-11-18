using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Payments.IntegrationTests.Infrastructure;

namespace WalletPK.Payments.IntegrationTests;

/// <summary>M6.1 and M6.2: sending money, asking for it and splitting bills, with the Ledger answering on Kafka.</summary>
[Collection(PaymentsCollection.Name)]
public class WalletPaymentTests(PaymentsFactory factory) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        factory.Clock.Reset();
        return ValueTask.CompletedTask;
    }

    private sealed record Person(Guid Id, string Phone, HttpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private Person NewPerson()
    {
        var id = Guid.NewGuid();
        return new Person(id, PaymentsFactory.PhoneOf(id), factory.ClientFor(id));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Cancel);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient client, string path, string property, string value, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement item;
        do
        {
            item = await GetAsync(client, path);
            if (item.GetProperty(property).GetString() == value)
            {
                return item;
            }

            await Task.Delay(100, Cancel);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"{path} did not reach {property} = {value}: {item}");
    }

    private static string[] Steps(JsonElement item) =>
        [.. item.GetProperty("timeline").EnumerateArray().Select(step => step.GetProperty("state").GetString()!)];

    private async Task<Guid> RequestAsync(Person from, Person payer, long paisa, object? extra = null)
    {
        using var response = await PostAsync(from.Client, "/api/payments/requests", extra ?? new { fromWalletNumber = payer.Phone, amountPaisa = paisa, note = "Lunch" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    // ---- M6.1 ----

    [Fact]
    public async Task Sending_money_moves_it_in_one_ledger_journal_and_reports_the_fee()
    {
        using var sender = NewPerson();
        using var recipient = NewPerson();
        var key = Guid.NewGuid().ToString();

        using var response = await PostAsync(sender.Client, "/api/payments/p2p-transfers", new { toWalletNumber = "0" + recipient.Phone[3..], amountPaisa = 75_000, note = "Rent share" }, key);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await JsonAsync(response);
        var id = accepted.GetProperty("id").GetGuid();
        accepted.GetProperty("toWalletNumber").GetString().ShouldBe(recipient.Phone);

        var done = await WaitForAsync(sender.Client, $"/api/payments/p2p-transfers/{id}", "status", "Completed");
        done.GetProperty("feePaisa").GetInt64().ShouldBe(1_000);
        done.GetProperty("taxPaisa").GetInt64().ShouldBe(160);
        Steps(done).ShouldBe(["Sending", "Completed"]);

        var command = factory.Ledger.CommandsFor(id).OfType<TransferFunds>().Single();
        command.ShouldSatisfyAllConditions(
            () => command.SenderUserId.ShouldBe(sender.Id),
            () => command.ReplyTo.ShouldBe(id),
            () => command.ToWalletNumber.ShouldBe(recipient.Phone),
            () => command.AmountPaisa.ShouldBe(75_000));

        // The same key again is the same transfer; the money moved once.
        using var again = await PostAsync(sender.Client, "/api/payments/p2p-transfers", new { toWalletNumber = recipient.Phone, amountPaisa = 75_000 }, key);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe(id);
        factory.Ledger.Transferred.ContainsKey(id).ShouldBeTrue();

        var events = factory.ReadUntil(KafkaTopics.PaymentsEvents, read =>
            read.Any(e => e.EventType == "payments.p2p_transfer_changed" && e.Payload.GetProperty("transferId").GetGuid() == id));
        events.Single(e => e.EventType == "payments.p2p_transfer_changed" && e.Payload.GetProperty("transferId").GetGuid() == id)
            .Payload.GetProperty("status").GetString().ShouldBe("Completed");

        var mine = await GetAsync(sender.Client, "/api/payments/p2p-transfers");
        mine.EnumerateArray().Select(transfer => transfer.GetProperty("id").GetGuid()).ShouldBe([id]);
    }

    [Fact]
    public async Task A_transfer_the_ledger_refuses_fails_with_its_reason()
    {
        using var sender = NewPerson();
        using var recipient = NewPerson();
        factory.Ledger.RefuseTransfers(sender.Id, "ledger.insufficient_funds", "Not enough money in the wallet.");

        using var response = await PostAsync(sender.Client, "/api/payments/p2p-transfers", new { toWalletNumber = recipient.Phone, amountPaisa = 50_000 });
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var failed = await WaitForAsync(sender.Client, $"/api/payments/p2p-transfers/{id}", "status", "Failed");

        failed.GetProperty("failureCode").GetString().ShouldBe("ledger.insufficient_funds");
        factory.Ledger.Transferred.ContainsKey(id).ShouldBeFalse();
    }

    [Fact]
    public async Task A_lost_ledger_answer_is_asked_for_again_with_the_same_transfer_id()
    {
        using var sender = NewPerson();
        using var recipient = NewPerson();
        factory.Ledger.IgnoreNext(sender.Id, 1);

        using var response = await PostAsync(sender.Client, "/api/payments/p2p-transfers", new { toWalletNumber = recipient.Phone, amountPaisa = 20_000 });
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await WaitForAsync(sender.Client, $"/api/payments/p2p-transfers/{id}", "status", "Completed");

        factory.Ledger.CommandsFor(id).OfType<TransferFunds>().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData("self", 10_000, "payments.self")]
    [InlineData("12345", 10_000, "payments.invalid_wallet_number")]
    [InlineData("other", 50, "payments.amount_out_of_range")]
    public async Task Invalid_sends_are_refused(string to, long paisa, string code)
    {
        using var sender = NewPerson();
        var number = to switch { "self" => sender.Phone, "other" => "+923331112223", _ => to };

        using var response = await PostAsync(sender.Client, "/api/payments/p2p-transfers", new { toWalletNumber = number, amountPaisa = paisa });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    // ---- M6.2 Request money ----

    [Fact]
    public async Task A_request_is_paid_by_the_payer_with_money_moving_to_the_requester()
    {
        using var requester = NewPerson();
        using var payer = NewPerson();
        var id = await RequestAsync(requester, payer, 120_000);

        var incoming = await GetAsync(payer.Client, "/api/payments/requests?direction=incoming");
        var seen = incoming.EnumerateArray().Single(request => request.GetProperty("id").GetGuid() == id);
        seen.GetProperty("direction").GetString().ShouldBe("incoming");
        seen.GetProperty("requesterWalletNumber").GetString().ShouldBe(requester.Phone);
        seen.GetProperty("status").GetString().ShouldBe("Pending");

        using (var pay = await PostAsync(payer.Client, $"/api/payments/requests/{id}/pay", null))
        {
            pay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var paid = await WaitForAsync(requester.Client, $"/api/payments/requests/{id}", "status", "Paid");
        paid.GetProperty("direction").GetString().ShouldBe("outgoing");
        Steps(paid).ShouldBe(["Pending", "Paying", "Paid"]);

        var payment = factory.Ledger.Transferred.Values.Single(transfer => transfer.ReplyTo == id);
        payment.ShouldSatisfyAllConditions(
            () => payment.SenderUserId.ShouldBe(payer.Id),
            () => payment.ToWalletNumber.ShouldBe(requester.Phone),
            () => payment.AmountPaisa.ShouldBe(120_000));

        // Paying again does nothing.
        using var twice = await PostAsync(payer.Client, $"/api/payments/requests/{id}/pay", null);
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var outgoing = await GetAsync(requester.Client, "/api/payments/requests?direction=outgoing");
        outgoing.EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Paid");
    }

    [Fact]
    public async Task A_failed_payment_leaves_the_request_open_for_another_try()
    {
        using var requester = NewPerson();
        using var payer = NewPerson();
        var id = await RequestAsync(requester, payer, 30_000);
        factory.Ledger.RefuseTransfers(payer.Id, "ledger.insufficient_funds", "Not enough money in the wallet.");

        using (await PostAsync(payer.Client, $"/api/payments/requests/{id}/pay", null))
        {
        }

        var open = await WaitForAsync(payer.Client, $"/api/payments/requests/{id}", "lastFailureCode", "ledger.insufficient_funds");
        open.GetProperty("status").GetString().ShouldBe("Pending");

        factory.Ledger.AllowTransfers(payer.Id);
        using (await PostAsync(payer.Client, $"/api/payments/requests/{id}/pay", null))
        {
        }

        var paid = await WaitForAsync(payer.Client, $"/api/payments/requests/{id}", "status", "Paid");
        Steps(paid).ShouldBe(["Pending", "Paying", "Pending", "Paying", "Paid"]);
        var attempts = factory.Ledger.Commands.Values.SelectMany(commands => commands).OfType<TransferFunds>().Where(command => command.ReplyTo == id).Select(command => command.TransferId).Distinct();
        attempts.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Only_the_payer_pays_or_declines_and_only_the_requester_cancels()
    {
        using var requester = NewPerson();
        using var payer = NewPerson();
        using var stranger = NewPerson();
        var declined = await RequestAsync(requester, payer, 10_000);
        var cancelled = await RequestAsync(requester, payer, 20_000);

        using (var response = await stranger.Client.GetAsync(new Uri($"/api/payments/requests/{declined}", UriKind.Relative), Cancel))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var response = await PostAsync(requester.Client, $"/api/payments/requests/{declined}/pay", null))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PostAsync(payer.Client, $"/api/payments/requests/{cancelled}/cancel", null))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var response = await PostAsync(payer.Client, $"/api/payments/requests/{declined}/decline", new { reason = "Already paid you in cash" }))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var view = await JsonAsync(response);
            view.GetProperty("status").GetString().ShouldBe("Declined");
            view.GetProperty("declineReason").GetString().ShouldBe("Already paid you in cash");
        }

        using (var response = await PostAsync(requester.Client, $"/api/payments/requests/{cancelled}/cancel", null))
        {
            (await JsonAsync(response)).GetProperty("status").GetString().ShouldBe("Cancelled");
        }

        factory.Ledger.Transferred.Values.ShouldNotContain(transfer => transfer.ReplyTo == declined || transfer.ReplyTo == cancelled);
    }

    [Fact]
    public async Task An_unpaid_request_expires()
    {
        using var requester = NewPerson();
        using var payer = NewPerson();
        var id = await RequestAsync(requester, payer, 10_000, new { fromWalletNumber = payer.Phone, amountPaisa = 10_000, expiresInHours = 1 });

        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        var expired = await WaitForAsync(requester.Client, $"/api/payments/requests/{id}", "status", "Expired");

        Steps(expired).ShouldBe(["Pending", "Expired"]);
        using var late = await PostAsync(payer.Client, $"/api/payments/requests/{id}/pay", null);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- M6.2 Split a bill ----

    [Fact]
    public async Task A_split_bill_asks_each_person_for_their_share_and_tracks_who_paid()
    {
        using var me = NewPerson();
        using var ali = NewPerson();
        using var sara = NewPerson();
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            title = "Dinner at Monal",
            totalPaisa = 300_100,
            participants = new[] { new { walletNumber = ali.Phone }, new { walletNumber = sara.Phone } },
            includeMe = true,
        };

        using var response = await PostAsync(me.Client, "/api/payments/splits", body, key);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
        var split = await JsonAsync(response);
        var splitId = split.GetProperty("id").GetGuid();
        split.GetProperty("ownSharePaisa").GetInt64().ShouldBe(100_033);
        split.GetProperty("shares").EnumerateArray().Select(share => share.GetProperty("amountPaisa").GetInt64()).Sum().ShouldBe(200_067);

        // Each sees their share as an incoming request.
        Guid ShareOf(JsonElement view, string phone) =>
            view.GetProperty("shares").EnumerateArray().Single(share => share.GetProperty("walletNumber").GetString() == phone).GetProperty("requestId").GetGuid();
        var aliShare = ShareOf(split, ali.Phone);
        var saraShare = ShareOf(split, sara.Phone);
        (await GetAsync(ali.Client, "/api/payments/requests?direction=incoming")).EnumerateArray().Single().GetProperty("splitTitle").GetString().ShouldBe("Dinner at Monal");

        using (await PostAsync(ali.Client, $"/api/payments/requests/{aliShare}/pay", null))
        {
        }

        using (await PostAsync(sara.Client, $"/api/payments/requests/{saraShare}/decline", null))
        {
        }

        await WaitForAsync(me.Client, $"/api/payments/requests/{aliShare}", "status", "Paid");
        var summary = await GetAsync(me.Client, $"/api/payments/splits/{splitId}");
        summary.GetProperty("paidCount").GetInt32().ShouldBe(1);
        summary.GetProperty("collectedPaisa").GetInt64().ShouldBe(ShareAmount(summary, ali.Phone));
        summary.GetProperty("outstandingPaisa").GetInt64().ShouldBe(0);

        // The same key returns the same split; my splits list it.
        using (var again = await PostAsync(me.Client, "/api/payments/splits", body, key))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await JsonAsync(again)).GetProperty("id").GetGuid().ShouldBe(splitId);
        }

        (await GetAsync(me.Client, "/api/payments/splits")).EnumerateArray().Select(view => view.GetProperty("id").GetGuid()).ShouldBe([splitId]);
        using var notMine = await ali.Client.GetAsync(new Uri($"/api/payments/splits/{splitId}", UriKind.Relative), Cancel);
        notMine.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static long ShareAmount(JsonElement split, string phone) =>
        split.GetProperty("shares").EnumerateArray().Single(share => share.GetProperty("walletNumber").GetString() == phone).GetProperty("amountPaisa").GetInt64();

    [Fact]
    public async Task Split_shares_must_add_up()
    {
        using var me = NewPerson();
        using var response = await PostAsync(me.Client, "/api/payments/splits", new
        {
            title = "Taxi",
            totalPaisa = 100_000,
            participants = new[] { new { walletNumber = "+923331112223", amountPaisa = (long?)70_000 }, new { walletNumber = "+923331112224", amountPaisa = (long?)50_000 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonAsync(response)).GetProperty("code").GetString().ShouldBe("payments.split_amounts");
    }
}
