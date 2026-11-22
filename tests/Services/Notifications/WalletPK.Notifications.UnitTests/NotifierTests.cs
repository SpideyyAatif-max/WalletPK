using WalletPK.Contracts.Notifications;
using WalletPK.Messaging.Events;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.Domain;

namespace WalletPK.Notifications.UnitTests;

/// <summary>An in-memory store for the use cases.</summary>
internal sealed class FakeStore : INotificationStore
{
    public List<Recipient> Recipients { get; } = [];

    public List<Notification> Notifications { get; } = [];

    public List<Delivery> Deliveries { get; } = [];

    public List<IntegrationEvent> Published { get; } = [];

    public Task<Recipient?> RecipientAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Recipients.SingleOrDefault(r => r.UserId == userId));

    public Task<Recipient?> RecipientByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) => Task.FromResult(Recipients.FirstOrDefault(r => r.PhoneNumber == phoneNumber));

    public Task<Guid?> AgentOwnerAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<bool> ExistsAsync(Guid userId, string dedupeKey, CancellationToken cancellationToken) =>
        Task.FromResult(Notifications.Any(n => n.UserId == userId && n.DedupeKey == dedupeKey));

    public Task<IReadOnlyList<Notification>> PageAsync(Guid userId, (DateTimeOffset At, Guid Id)? before, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Notification>>([.. Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAt).Take(limit)]);

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Notifications.Count(n => n.UserId == userId && n.ReadAt is null));

    public Task<Notification?> NotificationAsync(Guid userId, Guid id, CancellationToken cancellationToken) => Task.FromResult(Notifications.SingleOrDefault(n => n.Id == id && n.UserId == userId));

    public Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<Delivery>> DeliveriesForAsync(Guid userId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Delivery>>([.. Deliveries.Where(d => d.UserId == userId)]);

    public async Task<int> ForEachDueAsync(int limit, DateTimeOffset now, Func<Delivery, Task> send, CancellationToken cancellationToken)
    {
        var due = Deliveries.Where(d => d.Status == DeliveryStatuses.Pending && d.NextAttemptAt <= now).Take(limit).ToList();
        foreach (var delivery in due)
        {
            await send(delivery);
        }

        return due.Count;
    }

    public void Add(object entity)
    {
        switch (entity)
        {
            case Recipient recipient: Recipients.Add(recipient); break;
            case Notification notification: Notifications.Add(notification); break;
            case Delivery delivery: Deliveries.Add(delivery); break;
        }
    }

    public void Publish(IntegrationEvent message) => Published.Add(message);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>M11.4, M11.5: which channels a notification takes, and that a subject is notified once.</summary>
public class NotifierTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeStore _store = new();
    private readonly RealtimeOutbox _outbox = new();
    private readonly Notifier _notifier;
    private readonly Guid _user = Guid.NewGuid();

    public NotifierTests() => _notifier = new Notifier(_store, _outbox, new NotificationsOptions(), new FixedClock(_now));

    private Recipient Recipient(string language = "en", bool sms = true, bool email = true, string? address = null)
    {
        var recipient = new Recipient { UserId = _user, PhoneNumber = "+923001234567", Language = language, SmsEnabled = sms, EmailEnabled = email, Email = address };
        _store.Recipients.Add(recipient);
        return recipient;
    }

    private static NotifyRequest Received(Guid user, string key = "entry:w:1", DateTimeOffset? at = null, bool allowSms = true) => new(
        user,
        TemplateCatalog.MoneyReceived,
        new Dictionary<string, string> { ["amount"] = "Rs. 250.00", ["balance"] = "Rs. 250.00", ["counterparty"] = "+92300*****67" },
        key,
        at ?? _now,
        allowSms);

    [Fact]
    public async Task A_payment_goes_to_the_notification_centre_and_by_SMS_in_the_users_language_and_is_pushed_after_commit()
    {
        Recipient(language: "ur");

        var notification = await _notifier.NotifyAsync(Received(_user), TestContext.Current.CancellationToken);

        notification.ShouldNotBeNull();
        _store.Notifications.ShouldHaveSingleItem().Category.ShouldBe(Categories.Money);
        var sms = _store.Deliveries.ShouldHaveSingleItem();
        sms.Channel.ShouldBe(DeliveryChannels.Sms);
        sms.Destination.ShouldBe("+923001234567");
        sms.Language.ShouldBe("ur");
        sms.Body.ShouldStartWith("WalletPK: آپ کو +92300*****67 سے Rs. 250.00 موصول ہوئے");
        _outbox.DeliveriesQueued.ShouldBeTrue();
        _outbox.Take().Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_subject_is_notified_once()
    {
        Recipient();

        (await _notifier.NotifyAsync(Received(_user), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await _notifier.NotifyAsync(Received(_user), TestContext.Current.CancellationToken)).ShouldBeNull();

        _store.Notifications.Count.ShouldBe(1);
        _store.Deliveries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Turning_SMS_off_stops_payment_texts_but_never_security_messages()
    {
        Recipient(sms: false, email: false, address: "me@example.pk");

        await _notifier.NotifyAsync(Received(_user), TestContext.Current.CancellationToken);
        _store.Deliveries.ShouldBeEmpty();

        await _notifier.NotifyAsync(new NotifyRequest(_user, TemplateCatalog.AccountFrozen, new Dictionary<string, string>(), "freeze:1", _now), TestContext.Current.CancellationToken);
        _store.Deliveries.Select(d => d.Channel).ShouldBe([DeliveryChannels.Sms, DeliveryChannels.Email], ignoreOrder: true);
        _store.Deliveries.Single(d => d.Channel == DeliveryChannels.Email).Subject.ShouldBe("WalletPK: Your wallet is on hold");
    }

    [Fact]
    public async Task No_texts_for_merchant_and_agent_wallets_old_events_or_users_without_a_number()
    {
        Recipient();

        await _notifier.NotifyAsync(Received(_user, "entry:m:1", allowSms: false), TestContext.Current.CancellationToken);
        await _notifier.NotifyAsync(Received(_user, "entry:w:2", at: _now.AddHours(-2)), TestContext.Current.CancellationToken);
        await _notifier.NotifyAsync(Received(Guid.NewGuid(), "entry:x:1"), TestContext.Current.CancellationToken);

        _store.Notifications.Count.ShouldBe(3);
        _store.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Email_needs_an_address_and_the_setting_on()
    {
        Recipient(address: "me@example.pk");

        await _notifier.NotifyAsync(
            new NotifyRequest(_user, TemplateCatalog.KycUpgraded, new Dictionary<string, string> { ["tier"] = "L2" }, "kyc:L2", _now), TestContext.Current.CancellationToken);

        var email = _store.Deliveries.ShouldHaveSingleItem();
        email.Channel.ShouldBe(DeliveryChannels.Email);
        email.Destination.ShouldBe("me@example.pk");
        email.Body.ShouldStartWith("Your account is now L2.");
    }
}

/// <summary>M11.4, M11.5: sending with retries, and reporting the outcome.</summary>
public class DeliveryServiceTests
{
    private readonly FakeStore _store = new();
    private readonly FakeGateway _gateway = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly DeliveryService _service;

    public DeliveryServiceTests()
    {
        _service = new DeliveryService(_store, _gateway, new NotificationsOptions { MaxAttempts = 3, RetryInitialSeconds = 5 }, _clock);
        _store.Deliveries.Add(new Delivery
        {
            Id = Guid.NewGuid(), NotificationId = Guid.NewGuid(), UserId = Guid.NewGuid(), Channel = DeliveryChannels.Sms, Template = TemplateCatalog.MoneyReceived,
            Destination = "+923001234567", Language = "en", Body = "WalletPK: hello", NextAttemptAt = _clock.Now,
        });
    }

    private Delivery Delivery => _store.Deliveries[0];

    [Fact]
    public async Task A_sent_message_is_marked_and_reported_with_its_gateway_reference()
    {
        await _service.SendDueAsync(10, TestContext.Current.CancellationToken);

        Delivery.Status.ShouldBe(DeliveryStatuses.Sent);
        Delivery.ProviderReference.ShouldBe("REF1");
        Delivery.Attempts.ShouldBe(1);
        _gateway.MessageIds.ShouldBe([Delivery.Id]);
        var reported = _store.Published.ShouldHaveSingleItem().ShouldBeOfType<NotificationDeliveryChanged>();
        reported.Status.ShouldBe(DeliveryStatuses.Sent);
    }

    [Fact]
    public async Task An_unavailable_gateway_is_retried_later_with_the_same_message_id_then_given_up()
    {
        _gateway.Next = GatewayOutcome.Unavailable;

        await _service.SendDueAsync(10, TestContext.Current.CancellationToken);
        Delivery.Status.ShouldBe(DeliveryStatuses.Pending);
        Delivery.NextAttemptAt.ShouldBe(_clock.Now.AddSeconds(5));
        (await _service.SendDueAsync(10, TestContext.Current.CancellationToken)).ShouldBe(0, "not due yet");

        _clock.Now = Delivery.NextAttemptAt;
        await _service.SendDueAsync(10, TestContext.Current.CancellationToken);
        Delivery.NextAttemptAt.ShouldBe(_clock.Now.AddSeconds(10));

        _clock.Now = Delivery.NextAttemptAt;
        await _service.SendDueAsync(10, TestContext.Current.CancellationToken);
        Delivery.Status.ShouldBe(DeliveryStatuses.Failed);
        Delivery.Attempts.ShouldBe(3);
        _gateway.MessageIds.Distinct().ShouldBe([Delivery.Id]);
        _store.Published.ShouldHaveSingleItem().ShouldBeOfType<NotificationDeliveryChanged>().Status.ShouldBe(DeliveryStatuses.Failed);
    }

    [Fact]
    public async Task A_refused_message_is_not_retried()
    {
        _gateway.Next = GatewayOutcome.Rejected;

        await _service.SendDueAsync(10, TestContext.Current.CancellationToken);

        Delivery.Status.ShouldBe(DeliveryStatuses.Failed);
        Delivery.Attempts.ShouldBe(1);
    }

    private sealed class FakeGateway : IMessageGateway
    {
        public GatewayOutcome Next { get; set; } = GatewayOutcome.Sent;

        public List<Guid> MessageIds { get; } = [];

        public Task<GatewayResult> SendSmsAsync(Guid messageId, string to, string text, CancellationToken cancellationToken)
        {
            MessageIds.Add(messageId);
            return Task.FromResult(new GatewayResult(Next, Next == GatewayOutcome.Sent ? "REF1" : null, Next == GatewayOutcome.Sent ? null : "down"));
        }

        public Task<GatewayResult> SendEmailAsync(Guid messageId, string to, string subject, string body, CancellationToken cancellationToken) =>
            SendSmsAsync(messageId, to, body, cancellationToken);
    }
}
