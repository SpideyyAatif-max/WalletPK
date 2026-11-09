using WalletPK.Common.Domain;
using WalletPK.Ledger.Application.Events;
using WalletPK.Messaging.Events;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Ledger.Infrastructure.Persistence;

/// <summary>Plugs the Ledger event mapping into the shared outbox interceptor.</summary>
internal sealed class LedgerIntegrationEventMapper : IIntegrationEventMapper
{
    public IntegrationEvent? Map(IDomainEvent domainEvent) => LedgerIntegrationEvents.FromDomainEvent(domainEvent);
}
