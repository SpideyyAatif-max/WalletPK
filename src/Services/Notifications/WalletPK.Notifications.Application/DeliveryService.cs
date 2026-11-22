using WalletPK.Contracts.Notifications;

namespace WalletPK.Notifications.Application;

/// <summary>
/// Sends due SMS and email (M11.4, M11.5). Each delivery is locked while it is sent, so two senders never take
/// the same one; it goes to the gateway with its own ID as the message ID, so if a call times out after the
/// gateway accepted it, the retry gets the first message back and the customer is texted once. Failures are
/// retried with growing waits, then given up (the in-app copy stays).
/// </summary>
public sealed class DeliveryService(INotificationStore store, IMessageGateway gateway, NotificationsOptions options, TimeProvider timeProvider)
{
    /// <summary>Sends up to <paramref name="limit"/> due deliveries; returns how many it tried.</summary>
    public Task<int> SendDueAsync(int limit, CancellationToken cancellationToken) =>
        store.ForEachDueAsync(limit, timeProvider.GetUtcNow(), delivery => SendAsync(delivery, cancellationToken), cancellationToken);

    private async Task SendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        delivery.Attempts++;
        var result = delivery.Channel == DeliveryChannels.Email
            ? await gateway.SendEmailAsync(delivery.Id, delivery.Destination, delivery.Subject ?? "WalletPK", delivery.Body, cancellationToken)
            : await gateway.SendSmsAsync(delivery.Id, delivery.Destination, delivery.Body, cancellationToken);
        var now = timeProvider.GetUtcNow();

        switch (result.Outcome)
        {
            case GatewayOutcome.Sent:
                delivery.Status = DeliveryStatuses.Sent;
                delivery.SentAt = now;
                delivery.ProviderReference = result.Reference;
                delivery.LastError = null;
                Report(delivery, now);
                break;
            case GatewayOutcome.Unavailable when delivery.Attempts < options.MaxAttempts:
                var wait = Math.Min(options.RetryMaxSeconds, options.RetryInitialSeconds * Math.Pow(2, delivery.Attempts - 1));
                delivery.NextAttemptAt = now.AddSeconds(wait);
                delivery.LastError = Trim(result.Detail);
                break;
            default:
                delivery.Status = DeliveryStatuses.Failed;
                delivery.LastError = Trim(result.Detail);
                Report(delivery, now);
                break;
        }
    }

    private void Report(Delivery delivery, DateTimeOffset now) =>
        store.Publish(new NotificationDeliveryChanged(
            delivery.Id, delivery.NotificationId, delivery.UserId, delivery.Channel, delivery.Template, delivery.Status, delivery.Attempts, now));

    private static string? Trim(string? detail) => detail is { Length: > 500 } ? detail[..500] : detail;
}
