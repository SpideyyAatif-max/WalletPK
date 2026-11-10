using WalletPK.Common.Domain;
using WalletPK.Messaging.Events;

namespace WalletPK.Persistence.Outbox;

/// <summary>
/// Decides which of a service's domain events become public integration events. Each service
/// implements this once; the shared <see cref="DomainEventOutboxInterceptor"/> calls it on every save.
/// </summary>
public interface IIntegrationEventMapper
{
    /// <summary>
    /// The integration events to publish for <paramref name="domainEvent"/>: none to keep it internal,
    /// or several when one change interests different consumers (a journal, and each wallet it touched).
    /// </summary>
    IEnumerable<IntegrationEvent> Map(IDomainEvent domainEvent);
}
