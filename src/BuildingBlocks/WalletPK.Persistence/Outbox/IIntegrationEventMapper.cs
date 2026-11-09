using WalletPK.Common.Domain;
using WalletPK.Messaging.Events;

namespace WalletPK.Persistence.Outbox;

/// <summary>
/// Decides which of a service's domain events become public integration events. Each service
/// implements this once; the shared <see cref="DomainEventOutboxInterceptor"/> calls it on every save.
/// </summary>
public interface IIntegrationEventMapper
{
    /// <summary>The integration event to publish for <paramref name="domainEvent"/>, or null to keep it internal.</summary>
    IntegrationEvent? Map(IDomainEvent domainEvent);
}
