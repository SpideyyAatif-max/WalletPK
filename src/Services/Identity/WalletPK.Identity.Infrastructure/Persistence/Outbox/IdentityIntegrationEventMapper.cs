using WalletPK.Common.Domain;
using WalletPK.Identity.Application.Events;
using WalletPK.Messaging.Events;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Identity.Infrastructure.Persistence.Outbox;

/// <summary>Plugs the Identity event mapping into the shared outbox interceptor.</summary>
internal sealed class IdentityIntegrationEventMapper : IIntegrationEventMapper
{
    public IEnumerable<IntegrationEvent> Map(IDomainEvent domainEvent) =>
        IdentityIntegrationEvents.FromDomainEvent(domainEvent) is { } integrationEvent ? [integrationEvent] : [];
}
