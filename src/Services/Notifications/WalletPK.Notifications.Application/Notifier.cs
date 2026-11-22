using WalletPK.Notifications.Domain;

namespace WalletPK.Notifications.Application;

/// <param name="UserId">Who it is for.</param>
/// <param name="Template">A key from <see cref="TemplateCatalog"/>.</param>
/// <param name="Parameters">The template's placeholders (see <see cref="MessageRenderer"/>).</param>
/// <param name="DedupeKey">What it is about; the same key for the same user is notified once (M11.5).</param>
/// <param name="OccurredAt">When the event happened (old events get no SMS or email).</param>
/// <param name="AllowSms">False for wallets whose owners get no texts per payment (merchant and agent wallets).</param>
public sealed record NotifyRequest(Guid UserId, string Template, IReadOnlyDictionary<string, string> Parameters, string DedupeKey, DateTimeOffset OccurredAt, bool AllowSms = true);

/// <summary>
/// Creates notifications (M11.2-M11.5): once per user and subject, in the notification centre always, by SMS
/// and email when the template, the user's settings and the event's age allow, and pushed live after commit.
/// </summary>
public sealed class Notifier(INotificationStore store, RealtimeOutbox outbox, NotificationsOptions options, TimeProvider timeProvider)
{
    public async Task<Notification?> NotifyAsync(NotifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = TemplateCatalog.Get(request.Template);
        if (await store.ExistsAsync(request.UserId, request.DedupeKey, cancellationToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var recipient = await store.RecipientAsync(request.UserId, cancellationToken);
        var language = recipient?.Language ?? Languages.English;
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = request.UserId,
            Template = template.Key,
            Category = template.Category,
            Parameters = new Dictionary<string, string>(request.Parameters, StringComparer.Ordinal),
            DedupeKey = request.DedupeKey,
            CreatedAt = now,
        };
        store.Add(notification);

        if (recipient is not null && now - request.OccurredAt <= TimeSpan.FromMinutes(options.SendWindowMinutes))
        {
            var wording = MessageRenderer.Render(template, language, notification.Parameters);
            if (template.Channels.HasFlag(Channels.Sms) && request.AllowSms && recipient.PhoneNumber is { } phone && (template.Security || recipient.SmsEnabled))
            {
                Queue(notification, DeliveryChannels.Sms, phone, language, null, $"WalletPK: {wording.Body}", now);
            }

            if (template.Channels.HasFlag(Channels.Email) && recipient.Email is { } email && (template.Security || recipient.EmailEnabled))
            {
                Queue(notification, DeliveryChannels.Email, email, language, $"WalletPK: {wording.Title}", EmailBody(wording, language), now);
            }
        }

        var unread = await store.UnreadCountAsync(request.UserId, cancellationToken) + 1;
        var view = NotificationCentre.View(notification, language);
        outbox.Push(realtime => realtime.NotificationAsync(request.UserId, new LiveNotification(view, unread)));
        return notification;
    }

    private void Queue(Notification notification, string channel, string destination, string language, string? subject, string body, DateTimeOffset now)
    {
        store.Add(new Delivery
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notification.Id,
            UserId = notification.UserId,
            Channel = channel,
            Template = notification.Template,
            Destination = destination,
            Language = language,
            Subject = subject,
            Body = body,
            NextAttemptAt = now,
            CreatedAt = now,
        });
        outbox.DeliveryQueued();
    }

    private static string EmailBody(Wording wording, string language) =>
        language == Languages.Urdu
            ? $"{wording.Body}\n\nWalletPK\nیہ ای میل آپ کو اس لیے ملی کیونکہ آپ کی WalletPK سیٹنگز میں ای میل اطلاعات فعال ہیں۔"
            : $"{wording.Body}\n\nWalletPK\nYou get this email because email notifications are on in your WalletPK settings.";
}

/// <summary>Who to notify, as learned from events (phone numbers, agent owners).</summary>
public sealed class Recipients(INotificationStore store, TimeProvider timeProvider)
{
    /// <summary>The user's row, created if missing; the phone number is filled or updated when given.</summary>
    public async Task<Recipient> EnsureAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken)
    {
        var recipient = await store.RecipientAsync(userId, cancellationToken);
        if (recipient is null)
        {
            recipient = new Recipient { UserId = userId, PhoneNumber = phoneNumber, UpdatedAt = timeProvider.GetUtcNow() };
            store.Add(recipient);
        }
        else if (phoneNumber is not null && recipient.PhoneNumber != phoneNumber)
        {
            recipient.PhoneNumber = phoneNumber;
            recipient.UpdatedAt = timeProvider.GetUtcNow();
        }

        return recipient;
    }

    public async Task SetAgentOwnerAsync(Guid agentId, Guid userId, CancellationToken cancellationToken)
    {
        if (await store.AgentOwnerAsync(agentId, cancellationToken) is null)
        {
            store.Add(new AgentOwner { AgentId = agentId, UserId = userId });
        }
    }
}
