using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Notifications;

/// <summary>
/// An SMS or email notification was sent, or given up on after its retries (M11.4). For the Ops dashboard
/// and delivery reports; the in-app copy is always kept regardless.
/// </summary>
/// <param name="DeliveryId">The delivery (also the idempotency key sent to the SMS or email gateway).</param>
/// <param name="NotificationId">The notification it delivers.</param>
/// <param name="UserId">Who it was for.</param>
/// <param name="Channel">"Sms" or "Email".</param>
/// <param name="Template">For example "money_received".</param>
/// <param name="Status">"Sent" or "Failed".</param>
/// <param name="Attempts">How many times the gateway was called.</param>
/// <param name="ChangedAt">When it was sent or given up on.</param>
[IntegrationEvent("notifications.delivery_changed", KafkaTopics.NotificationsEvents)]
public sealed record NotificationDeliveryChanged(
    Guid DeliveryId,
    Guid NotificationId,
    Guid UserId,
    string Channel,
    string Template,
    string Status,
    int Attempts,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}
