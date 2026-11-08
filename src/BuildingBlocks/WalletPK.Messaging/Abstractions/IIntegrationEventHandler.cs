using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Reacts to one type of integration event, for example the Notification service handling
/// "ledger.money_transferred". Handlers must be idempotent: the inbox prevents most duplicates,
/// but a handler should still be safe to run twice.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, EventContext context, CancellationToken cancellationToken);
}
