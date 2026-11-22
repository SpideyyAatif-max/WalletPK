using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Messaging.Events;

namespace WalletPK.Notifications.Application;

// ---------------------------------------------------------------------------------------------------
// Stored data
// ---------------------------------------------------------------------------------------------------

/// <summary>Who notifications go to and how: one row per user, created from their events or their settings.</summary>
public sealed class Recipient
{
    public Guid UserId { get; set; }

    /// <summary>E.164; from identity and wallet events, or the user's access token.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Only set by the user (identity does not hold email addresses).</summary>
    public string? Email { get; set; }

    /// <summary>"en" or "ur".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Text messages other than security ones.</summary>
    public bool SmsEnabled { get; set; } = true;

    /// <summary>Emails other than security ones (when there is an address).</summary>
    public bool EmailEnabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which user owns an agent (agent events name the agent; notifications go to its owner).</summary>
public sealed class AgentOwner
{
    public Guid AgentId { get; set; }

    public Guid UserId { get; set; }
}

/// <summary>
/// One notification in a user's notification centre. It stores the template and its parameters rather than
/// text, so it shows in whichever language the user picks.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Template { get; set; } = "";

    public string Category { get; set; } = "";

    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>
    /// What it is about, for example "entry:{walletId}:{sequence}"; unique per user, so the same thing never
    /// makes two notifications however many times its event arrives (M11.5).
    /// </summary>
    public string DedupeKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

public static class DeliveryChannels
{
    public const string Sms = "Sms";
    public const string Email = "Email";
}

public static class DeliveryStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

/// <summary>
/// One SMS or email for a notification (at most one of each per notification). Its ID is the message ID the
/// gateway gets, so a retry after a timeout cannot send it twice (M11.5).
/// </summary>
public sealed class Delivery
{
    public Guid Id { get; set; }

    public Guid NotificationId { get; set; }

    public Guid UserId { get; set; }

    public string Channel { get; set; } = "";

    public string Template { get; set; } = "";

    public string Destination { get; set; } = "";

    /// <summary>The language it was written in (the user's choice when it was created).</summary>
    public string Language { get; set; } = "";

    public string? Subject { get; set; }

    public string Body { get; set; } = "";

    public string Status { get; set; } = DeliveryStatuses.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The gateway's reference once sent.</summary>
    public string? ProviderReference { get; set; }

    public string? LastError { get; set; }
}

// ---------------------------------------------------------------------------------------------------
// What the app and the hubs see
// ---------------------------------------------------------------------------------------------------

/// <summary>A notification rendered in the reader's language.</summary>
/// <param name="Id">The notification.</param>
/// <param name="Template">For example "money_received" (for an icon).</param>
/// <param name="Category">See <c>Categories</c>.</param>
/// <param name="Title">A few words.</param>
/// <param name="Body">The message.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Read">Whether the user has seen it.</param>
public sealed record NotificationView(Guid Id, string Template, string Category, string Title, string Body, DateTimeOffset CreatedAt, bool Read);

/// <param name="Items">Newest first.</param>
/// <param name="UnreadCount">All unread notifications, not only this page's.</param>
/// <param name="NextBefore">Pass as ?before= for the next (older) page; null on the last page.</param>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int UnreadCount, string? NextBefore);

/// <summary>The user's notification settings.</summary>
/// <param name="Language">"en" or "ur".</param>
/// <param name="SmsEnabled">Text messages other than security ones.</param>
/// <param name="EmailEnabled">Emails other than security ones.</param>
/// <param name="Email">Where emails go; none are sent without one.</param>
/// <param name="PhoneNumber">Where text messages go (the wallet's mobile number; read only).</param>
public sealed record NotificationSettings(string Language, bool SmsEnabled, bool EmailEnabled, string? Email, string? PhoneNumber);

/// <param name="Language">"en" or "ur"; null keeps it.</param>
/// <param name="SmsEnabled">Null keeps it.</param>
/// <param name="EmailEnabled">Null keeps it.</param>
/// <param name="Email">An address, "" to remove it, null to keep it.</param>
public sealed record UpdateSettingsRequest(string? Language, bool? SmsEnabled, bool? EmailEnabled, string? Email);

/// <summary>An SMS or email as sent (development: to read what the app sent).</summary>
public sealed record SentMessageView(Guid Id, string Channel, string To, string? Subject, string Body, string Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

/// <summary>A new entry on one of the user's wallets, pushed live (same shape as the Ledger's statement line).</summary>
/// <param name="WalletId">The wallet.</param>
/// <param name="WalletType">"CustomerWallet", "MerchantWallet" or "AgentWallet".</param>
/// <param name="Sequence">The entry's number in the wallet's history.</param>
/// <param name="Balance">The balance right after it.</param>
/// <param name="Entry">The entry as a statement line.</param>
public sealed record LiveWalletEntry(Guid WalletId, string WalletType, long Sequence, Money Balance, LiveStatementLine Entry);

/// <summary>Same shape as GET /api/wallets/me/transactions items.</summary>
public sealed record LiveStatementLine(long Sequence, Guid TransactionId, string Type, string Title, string Direction, Money Amount, Money BalanceAfter, DateTimeOffset PostedAt);

/// <summary>A new notification, pushed live with the new unread count.</summary>
public sealed record LiveNotification(NotificationView Notification, int UnreadCount);

/// <summary>A payment of the user changed (the app refreshes that payment's screen).</summary>
/// <param name="Kind">"p2p", "bank", "bill", "topup", "merchant", "agent" or "request".</param>
/// <param name="Id">The payment's ID in its service.</param>
/// <param name="Status">Its new status.</param>
/// <param name="Step">Where its Saga is, when it has one.</param>
public sealed record PaymentUpdate(string Kind, Guid Id, string Status, string? Step);

/// <summary>Something operations staff should see now, on the Ops live feed.</summary>
/// <param name="Id">Unique per event.</param>
/// <param name="Kind">For example "fraud.alert", "account.frozen", "dead_letter", "agent.float_low".</param>
/// <param name="Severity">"Low", "Medium" or "High".</param>
/// <param name="Title">One line.</param>
/// <param name="Detail">More, when there is any.</param>
/// <param name="UserId">The customer it is about, if any.</param>
/// <param name="At">When it happened.</param>
public sealed record OpsEvent(Guid Id, string Kind, string Severity, string Title, string? Detail, Guid? UserId, DateTimeOffset At);

// ---------------------------------------------------------------------------------------------------
// Ports
// ---------------------------------------------------------------------------------------------------

/// <summary>Notifications' data (PostgreSQL).</summary>
public interface INotificationStore
{
    Task<Recipient?> RecipientAsync(Guid userId, CancellationToken cancellationToken);

    Task<Recipient?> RecipientByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);

    Task<Guid?> AgentOwnerAsync(Guid agentId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid userId, string dedupeKey, CancellationToken cancellationToken);

    /// <summary>Newest first, older than the (createdAt, id) cursor when given.</summary>
    Task<IReadOnlyList<Notification>> PageAsync(Guid userId, (DateTimeOffset At, Guid Id)? before, int limit, CancellationToken cancellationToken);

    Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Tracked, for changes.</summary>
    Task<Notification?> NotificationAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The SMS and emails sent to a user, newest first.</summary>
    Task<IReadOnlyList<Delivery>> DeliveriesForAsync(Guid userId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="send"/> for up to <paramref name="limit"/> due deliveries, each in its own
    /// transaction with its row locked (other senders skip it), saving what it changed. Returns how many ran.
    /// </summary>
    Task<int> ForEachDueAsync(int limit, DateTimeOffset now, Func<Delivery, Task> send, CancellationToken cancellationToken);

    void Add(object entity);

    void Publish(IntegrationEvent message);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public enum GatewayOutcome
{
    Sent,

    /// <summary>The gateway refused the message (bad number, bad address): retrying will not help.</summary>
    Rejected,

    /// <summary>Down, timed out or busy: try again later (with the same message ID).</summary>
    Unavailable,
}

public sealed record GatewayResult(GatewayOutcome Outcome, string? Reference, string? Detail);

/// <summary>The SMS and email gateways.</summary>
public interface IMessageGateway
{
    Task<GatewayResult> SendSmsAsync(Guid messageId, string to, string text, CancellationToken cancellationToken);

    Task<GatewayResult> SendEmailAsync(Guid messageId, string to, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>Live pushes to browsers (SignalR, across instances through the Redis backplane).</summary>
public interface IRealtime
{
    Task EntryPostedAsync(Guid userId, LiveWalletEntry entry);

    Task NotificationAsync(Guid userId, LiveNotification notification);

    Task PaymentChangedAsync(Guid userId, PaymentUpdate update);

    Task OpsAsync(OpsEvent opsEvent);
}

/// <summary>Settings, from "Notifications".</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// SMS and email only for events at most this old: after an outage the app shows what was missed, but
    /// nobody gets texts about old payments.
    /// </summary>
    public int SendWindowMinutes { get; set; } = 60;

    /// <summary>Attempts per SMS or email before it is given up.</summary>
    public int MaxAttempts { get; set; } = 6;

    /// <summary>First retry after this; doubles each time.</summary>
    public int RetryInitialSeconds { get; set; } = 5;

    /// <summary>Longest wait between retries.</summary>
    public int RetryMaxSeconds { get; set; } = 300;

    /// <summary>How often the sender looks for due messages when nothing wakes it.</summary>
    public int SenderPollMilliseconds { get; set; } = 1000;

    /// <summary>
    /// A new consumer group starts at the newest events, so the first deployment does not notify everyone
    /// about the past. Tests set false.
    /// </summary>
    public bool StartFromLatest { get; set; } = true;

    public bool IsValid() =>
        SendWindowMinutes > 0 && MaxAttempts is > 0 and <= 50 && RetryInitialSeconds > 0 && RetryMaxSeconds >= RetryInitialSeconds
        && SenderPollMilliseconds is >= 20 and <= 60_000;
}

public static class NotificationErrors
{
    public static Error NotFound { get; } = Error.NotFound("notifications.not_found", "No such notification.");

    public static Error UnknownLanguage { get; } = Error.Validation("notifications.unknown_language", "language must be \"en\" or \"ur\".");

    public static Error InvalidEmail { get; } = Error.Validation("notifications.invalid_email", "email must be an email address (at most 254 characters).");

    public static Error InvalidCursor { get; } = Error.Validation("notifications.invalid_cursor", "before is not a cursor from an earlier page.");
}
