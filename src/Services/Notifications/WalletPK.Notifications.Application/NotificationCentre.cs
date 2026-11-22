using System.Globalization;
using System.Text.RegularExpressions;
using WalletPK.Common.Results;
using WalletPK.Notifications.Domain;

namespace WalletPK.Notifications.Application;

/// <summary>
/// The notification centre and settings (M11.4): the user's notifications in their language, read and
/// unread, their language and channels, and (in development) the SMS and emails they were sent.
/// </summary>
public sealed partial class NotificationCentre(INotificationStore store, TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<Result<NotificationPage>> PageAsync(Guid userId, string? before, int? limit, CancellationToken cancellationToken)
    {
        (DateTimeOffset, Guid)? cursor = null;
        if (before is not null)
        {
            if (ParseCursor(before) is not { } parsed)
            {
                return NotificationErrors.InvalidCursor;
            }

            cursor = parsed;
        }

        var size = Math.Clamp(limit ?? 20, 1, MaxPageSize);
        var language = (await store.RecipientAsync(userId, cancellationToken))?.Language ?? Languages.English;
        var items = await store.PageAsync(userId, cursor, size + 1, cancellationToken);
        var page = items.Take(size).ToList();
        var next = items.Count > size ? Cursor(page[^1]) : null;
        return new NotificationPage([.. page.Select(item => View(item, language))], await store.UnreadCountAsync(userId, cancellationToken), next);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken) => store.UnreadCountAsync(userId, cancellationToken);

    /// <summary>Marks one notification read; returns the unread count after it.</summary>
    public async Task<Result<int>> MarkReadAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        if (await store.NotificationAsync(userId, id, cancellationToken) is not { } notification)
        {
            return NotificationErrors.NotFound;
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = timeProvider.GetUtcNow();
            await store.SaveChangesAsync(cancellationToken);
        }

        return await store.UnreadCountAsync(userId, cancellationToken);
    }

    /// <summary>Marks every notification read; returns how many changed.</summary>
    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken cancellationToken) =>
        store.MarkAllReadAsync(userId, timeProvider.GetUtcNow(), cancellationToken);

    /// <param name="userId">The signed-in user.</param>
    /// <param name="phoneNumber">From the access token: fills the number for users the service has not heard of yet.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NotificationSettings> SettingsAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken)
    {
        var recipient = await EnsureAsync(userId, phoneNumber, cancellationToken);
        return Settings(recipient);
    }

    /// <param name="userId">The signed-in user.</param>
    /// <param name="phoneNumber">From the access token.</param>
    /// <param name="request">Only the fields given change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<Result<NotificationSettings>> UpdateSettingsAsync(Guid userId, string? phoneNumber, UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Language is not null && !Languages.IsKnown(request.Language))
        {
            return NotificationErrors.UnknownLanguage;
        }

        var email = request.Email?.Trim();
        if (email is { Length: > 0 } && (email.Length > 254 || !EmailAddress().IsMatch(email)))
        {
            return NotificationErrors.InvalidEmail;
        }

        var recipient = await EnsureAsync(userId, phoneNumber, cancellationToken);
        recipient.Language = request.Language ?? recipient.Language;
        recipient.SmsEnabled = request.SmsEnabled ?? recipient.SmsEnabled;
        recipient.EmailEnabled = request.EmailEnabled ?? recipient.EmailEnabled;
        if (email is not null)
        {
            recipient.Email = email.Length == 0 ? null : email.ToLowerInvariant();
        }

        recipient.UpdatedAt = timeProvider.GetUtcNow();
        await store.SaveChangesAsync(cancellationToken);
        return Settings(recipient);
    }

    /// <summary>The SMS and emails sent to the user, newest first (development).</summary>
    public async Task<IReadOnlyList<SentMessageView>> SentMessagesAsync(Guid userId, CancellationToken cancellationToken) =>
        [.. (await store.DeliveriesForAsync(userId, 50, cancellationToken)).Select(delivery => new SentMessageView(
            delivery.Id, delivery.Channel, delivery.Destination, delivery.Subject, delivery.Body, delivery.Status, delivery.Attempts, delivery.CreatedAt, delivery.SentAt))];

    /// <summary>A notification in <paramref name="language"/>.</summary>
    public static NotificationView View(Notification notification, string language)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (TemplateCatalog.Find(notification.Template) is not { } template)
        {
            return new(notification.Id, notification.Template, notification.Category, notification.Template, "", notification.CreatedAt, notification.ReadAt is not null);
        }

        var wording = MessageRenderer.Render(template, language, notification.Parameters);
        return new(notification.Id, notification.Template, notification.Category, wording.Title, wording.Body, notification.CreatedAt, notification.ReadAt is not null);
    }

    private async Task<Recipient> EnsureAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken)
    {
        if (await store.RecipientAsync(userId, cancellationToken) is { } recipient)
        {
            if (recipient.PhoneNumber is null && phoneNumber is not null)
            {
                recipient.PhoneNumber = phoneNumber;
                await store.SaveChangesAsync(cancellationToken);
            }

            return recipient;
        }

        recipient = new Recipient { UserId = userId, PhoneNumber = phoneNumber, UpdatedAt = timeProvider.GetUtcNow() };
        store.Add(recipient);
        await store.SaveChangesAsync(cancellationToken);
        return recipient;
    }

    private static NotificationSettings Settings(Recipient recipient) =>
        new(recipient.Language, recipient.SmsEnabled, recipient.EmailEnabled, recipient.Email, recipient.PhoneNumber);

    private static string Cursor(Notification notification) =>
        string.Create(CultureInfo.InvariantCulture, $"{notification.CreatedAt.UtcTicks}_{notification.Id:N}");

    private static (DateTimeOffset, Guid)? ParseCursor(string cursor)
    {
        var parts = cursor.Split('_');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks is > 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParseExact(parts[1], "N", out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailAddress();
}
