using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Contracts.Fraud;
using WalletPK.Contracts.Payments;
using WalletPK.Messaging;
using WalletPK.Notifications.Api.Realtime;
using WalletPK.Notifications.IntegrationTests.Infrastructure;
using static WalletPK.Notifications.IntegrationTests.Infrastructure.NotificationSteps;

namespace WalletPK.Notifications.IntegrationTests;

/// <summary>M11.2-M11.5: events become notifications, in the app, by SMS and email, in English and Urdu, once.</summary>
[Collection(NotificationsCollection.Name)]
public class NotificationTests(NotificationsFactory factory)
{
    [Fact]
    public async Task A_transfer_is_announced_to_both_sides_live_in_the_app_and_by_SMS_and_to_nobody_else()
    {
        var sender = await factory.CustomerAsync();
        var recipient = await factory.CustomerAsync();
        var bystander = await factory.CustomerAsync();
        await using var recipientTab = await factory.OpenAsync(WalletHub.Path, recipient.Id);
        await using var bystanderTab = await factory.OpenAsync(WalletHub.Path, bystander.Id);

        await factory.PublishAsync(Entry(sender, 2, "Debit", 25_000, 75_000, counterparty: recipient.Phone));
        await factory.PublishAsync(Entry(recipient, 1, "Credit", 25_000, 25_000, counterparty: sender.Phone));

        var entry = await recipientTab.NextAsync("EntryPosted");
        entry.GetProperty("walletId").GetGuid().ShouldBe(recipient.WalletId);
        entry.GetProperty("sequence").GetInt64().ShouldBe(1);
        entry.GetProperty("balance").GetProperty("minorUnits").GetInt64().ShouldBe(25_000);
        entry.GetProperty("entry").GetProperty("title").GetString().ShouldBe($"Received from {sender.Phone[..6]}*****{sender.Phone[^2..]}");
        entry.GetProperty("entry").GetProperty("direction").GetString().ShouldBe("In");

        var live = await recipientTab.NextAsync("NotificationReceived");
        live.GetProperty("notification").GetProperty("title").GetString().ShouldBe("Money received");
        live.GetProperty("unreadCount").GetInt32().ShouldBe(2, "the welcome and this");

        using var recipientClient = factory.ClientFor(recipient.Id);
        var received = await recipientClient.NotificationAsync("money_received");
        received.GetProperty("body").GetString().ShouldBe($"You received Rs. 250.00 from {sender.Phone[..6]}*****{sender.Phone[^2..]}. New balance: Rs. 250.00.");

        using var senderClient = factory.ClientFor(sender.Id);
        (await senderClient.NotificationAsync("money_sent")).GetProperty("body").GetString().ShouldStartWith("You sent Rs. 250.00 to");

        var texts = await WaitAsync(() => factory.Providers.TextsToAsync(recipient.Phone), list => list.Count >= 2, "welcome and payment SMS");
        texts[0].GetProperty("text").GetString().ShouldBe($"WalletPK: {received.GetProperty("body").GetString()}");

        await Task.Delay(300, Cancel);
        bystanderTab.Drain().ShouldBeEmpty();
    }

    [Fact]
    public async Task Choosing_Urdu_rewrites_the_notification_centre_and_later_texts_come_in_Urdu()
    {
        var customer = await factory.CustomerAsync();
        using var client = factory.ClientFor(customer.Id, phone: customer.Phone);

        using (var bad = await client.PutAsJsonAsync(new Uri("/api/notifications/settings", UriKind.Relative), new { language = "fr" }, Cancel))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var urdu = await client.PutAsJsonAsync(new Uri("/api/notifications/settings", UriKind.Relative), new { language = "ur" }, Cancel))
        {
            var settings = await urdu.JsonAsync();
            settings.GetProperty("language").GetString().ShouldBe("ur");
            settings.GetProperty("phoneNumber").GetString().ShouldBe(customer.Phone);
        }

        (await client.NotificationAsync("welcome")).GetProperty("title").GetString().ShouldBe("WalletPK میں خوش آمدید");

        await factory.PublishAsync(Entry(customer, 1, "Credit", 500_000, 500_000, journalType: "CashIn", description: "Cash-in at agent"));
        var notification = await client.NotificationAsync("wallet_credited");
        notification.GetProperty("body").GetString().ShouldBe("آپ کے والٹ میں Rs. 5,000.00 جمع ہوئے (کیش اِن)۔ نیا بیلنس: Rs. 5,000.00۔");

        var texts = await WaitAsync(() => factory.Providers.TextsToAsync(customer.Phone), list => list.Count >= 2, "the Urdu SMS");
        texts[0].GetProperty("text").GetString().ShouldBe($"WalletPK: {notification.GetProperty("body").GetString()}");
    }

    [Fact]
    public async Task A_redelivered_event_or_a_second_event_about_the_same_entry_notifies_and_texts_once()
    {
        var customer = await factory.CustomerAsync();
        var entry = Entry(customer, 1, "Credit", 10_000, 10_000, journalType: "CashIn");
        var envelope = factory.Wrap(entry);

        await factory.PublishAsync(envelope);
        await factory.PublishAsync(envelope);
        await factory.PublishAsync(entry with { TransactionId = entry.TransactionId });

        using var client = factory.ClientFor(customer.Id);
        await client.NotificationAsync("wallet_credited");
        await WaitAsync(() => factory.Providers.TextsToAsync(customer.Phone), list => list.Count >= 2, "the payment SMS");
        await Task.Delay(1500, Cancel);

        var page = await client.PageAsync();
        page.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("template").GetString() == "wallet_credited").ShouldBe(1);
        (await factory.Providers.TextsToAsync(customer.Phone)).Count.ShouldBe(2, "welcome + one payment text");
    }

    [Fact]
    public async Task When_the_gateway_texts_but_its_answer_is_lost_the_retry_does_not_text_again()
    {
        var customer = await factory.CustomerAsync();
        await WaitAsync(() => factory.Providers.TextsToAsync(customer.Phone), list => list.Count == 1, "the welcome SMS");
        using (var admin = factory.Providers.Admin())
        {
            using var faults = await admin.PutAsJsonAsync(new Uri("/admin/faults/sms", UriKind.Relative), new { acceptButTimeoutRate = 1, timeoutMs = 1500, durationSeconds = 3 }, Cancel);
            faults.EnsureSuccessStatusCode();
        }

        await factory.PublishAsync(Entry(customer, 1, "Credit", 20_000, 20_000, journalType: "CashIn"));

        using var client = factory.ClientFor(customer.Id);
        var sent = await WaitAsync(
            async () => (await client.GetFromJsonAsync<List<JsonElement>>(new Uri("/api/notifications/dev/messages", UriKind.Relative), Cancel))!,
            messages => messages.Any(m => m.GetProperty("body").GetString()!.Contains("Rs. 200.00", StringComparison.Ordinal) && m.GetProperty("status").GetString() == "Sent"),
            "the payment SMS sent after the fault",
            seconds: 30);
        sent.First(m => m.GetProperty("body").GetString()!.Contains("Rs. 200.00", StringComparison.Ordinal)).GetProperty("attempts").GetInt32().ShouldBeGreaterThan(1);

        var texts = await factory.Providers.TextsToAsync(customer.Phone);
        texts.Count(text => text.GetProperty("text").GetString()!.Contains("Rs. 200.00", StringComparison.Ordinal)).ShouldBe(1, "the gateway kept the first and ignored the retries");
    }

    [Fact]
    public async Task With_texts_off_payments_are_not_texted_but_a_frozen_wallet_is_texted_and_emailed_without_the_reason()
    {
        var customer = await factory.CustomerAsync();
        var email = $"user{Random.Shared.Next(100_000, 999_999)}@example.pk";
        using var client = factory.ClientFor(customer.Id);
        using (var settings = await client.PutAsJsonAsync(new Uri("/api/notifications/settings", UriKind.Relative), new { smsEnabled = false, emailEnabled = false, email }, Cancel))
        {
            settings.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await factory.PublishAsync(Entry(customer, 1, "Credit", 30_000, 30_000, journalType: "CashIn"));
        await client.NotificationAsync("wallet_credited");

        await factory.PublishAsync(new AccountFreezeChanged(customer.Id, true, "Mule ring 7 (SIM-swap)", "rules", DateTimeOffset.UtcNow));
        var frozen = await client.NotificationAsync("account_frozen");
        frozen.GetProperty("body").GetString()!.ShouldNotContain("Mule");

        var texts = await WaitAsync(() => factory.Providers.TextsToAsync(customer.Phone), list => list.Count >= 2, "the security SMS");
        texts.Count.ShouldBe(2, "welcome + frozen, not the payment");
        texts[0].GetProperty("text").GetString()!.ShouldStartWith("WalletPK: To protect your money");
        var emails = await WaitAsync(() => factory.Providers.EmailsToAsync(email), list => list.Count >= 1, "the security email");
        emails.ShouldHaveSingleItem().GetProperty("subject").GetString().ShouldBe("WalletPK: Your wallet is on hold");
    }

    [Fact]
    public async Task Old_events_after_an_outage_show_in_the_app_but_are_not_texted()
    {
        var customer = await factory.CustomerAsync();

        await factory.PublishAsync(Entry(customer, 1, "Credit", 40_000, 40_000, journalType: "CashIn", postedAt: DateTimeOffset.UtcNow.AddHours(-3)));

        using var client = factory.ClientFor(customer.Id);
        await client.NotificationAsync("wallet_credited");
        await Task.Delay(1000, Cancel);
        (await factory.Providers.TextsToAsync(customer.Phone)).Count.ShouldBe(1, "only the welcome");
    }

    [Fact]
    public async Task A_money_request_reaches_the_payer_and_a_failed_transfer_tells_the_sender_and_refreshes_their_screen()
    {
        var requester = await factory.CustomerAsync();
        var payer = await factory.CustomerAsync();
        await using var payerTab = await factory.OpenAsync(WalletHub.Path, payer.Id);

        var requestId = Guid.NewGuid();
        await factory.PublishAsync(new MoneyRequestChanged(requestId, requester.Id, requester.Phone, payer.Phone, "Pending", 150_000, null, "Requested", DateTimeOffset.UtcNow));
        using var payerClient = factory.ClientFor(payer.Id);
        (await payerClient.NotificationAsync("request_received")).GetProperty("body").GetString()
            .ShouldBe($"{requester.Phone[..6]}*****{requester.Phone[^2..]} has asked you for Rs. 1,500.00. Open WalletPK to pay or decline.");
        (await payerTab.NextAsync("PaymentChanged")).GetProperty("kind").GetString().ShouldBe("request");

        var transferId = Guid.NewGuid();
        await factory.PublishAsync(new P2PTransferChanged(transferId, payer.Id, requester.Phone, "Failed", 150_000, "Insufficient balance", DateTimeOffset.UtcNow));
        var update = await payerTab.NextAsync("PaymentChanged", payment => payment.GetProperty("id").GetGuid() == transferId);
        update.GetProperty("status").GetString().ShouldBe("Failed");
        (await payerClient.NotificationAsync("payment_failed")).GetProperty("body").GetString()
            .ShouldBe($"Your transfer of Rs. 1,500.00 to {requester.Phone[..6]}*****{requester.Phone[^2..]} did not go through: Insufficient balance. Any money held for it is back in your wallet.");
    }

    [Fact]
    public async Task The_notification_centre_pages_newest_first_and_tracks_what_was_read()
    {
        var customer = await factory.CustomerAsync();
        for (var i = 1; i <= 4; i++)
        {
            await factory.PublishAsync(Entry(customer, i, "Credit", i * 100, i * 1_000, journalType: "CashIn"));
        }

        using var client = factory.ClientFor(customer.Id);
        var all = await WaitAsync(() => client.PageAsync(), page => page.GetProperty("items").GetArrayLength() == 5, "five notifications");
        all.GetProperty("unreadCount").GetInt32().ShouldBe(5);

        var first = await client.PageAsync("?limit=2");
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        first.GetProperty("items")[0].GetProperty("body").GetString()!.ShouldContain("Rs. 40.00", Case.Sensitive, "newest first");
        var second = await client.PageAsync($"?limit=2&before={first.GetProperty("nextBefore").GetString()}");
        var third = await client.PageAsync($"?limit=2&before={second.GetProperty("nextBefore").GetString()}");
        third.GetProperty("items").GetArrayLength().ShouldBe(1);
        third.GetProperty("nextBefore").ValueKind.ShouldBe(JsonValueKind.Null);
        using (var bad = await client.GetAsync(new Uri("/api/notifications?before=nonsense", UriKind.Relative), Cancel))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var id = first.GetProperty("items")[0].GetProperty("id").GetGuid();
        using (var read = await client.PostAsync(new Uri($"/api/notifications/{id}/read", UriKind.Relative), null, Cancel))
        {
            (await read.JsonAsync()).GetProperty("count").GetInt32().ShouldBe(4);
        }

        using var stranger = factory.ClientFor(Guid.NewGuid());
        using (var notMine = await stranger.PostAsync(new Uri($"/api/notifications/{id}/read", UriKind.Relative), null, Cancel))
        {
            notMine.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (await client.PostAsync(new Uri("/api/notifications/read-all", UriKind.Relative), null, Cancel))
        {
        }

        (await client.GetFromJsonAsync<JsonElement>(new Uri("/api/notifications/unread-count", UriKind.Relative), Cancel)).GetProperty("count").GetInt32().ShouldBe(0);
        (await client.PageAsync()).GetProperty("items").EnumerateArray().ShouldAllBe(item => item.GetProperty("read").GetBoolean());
    }

    [Fact]
    public async Task Sent_and_failed_texts_are_reported_on_notifications_events()
    {
        var customer = await factory.CustomerAsync();

        var reports = factory.ReadUntil(KafkaTopics.NotificationsEvents, read =>
            read.Any(m => m.EventType == "notifications.delivery_changed" && m.Payload.GetProperty("userId").GetGuid() == customer.Id));

        var report = reports.First(m => m.Payload.GetProperty("userId").GetGuid() == customer.Id).Payload;
        report.GetProperty("channel").GetString().ShouldBe("Sms");
        report.GetProperty("template").GetString().ShouldBe("welcome");
        report.GetProperty("status").GetString().ShouldBe("Sent");
    }
}
