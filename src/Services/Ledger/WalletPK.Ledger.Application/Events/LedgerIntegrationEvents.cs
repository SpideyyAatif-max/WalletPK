using WalletPK.Common.Domain;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Messaging.Events;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Events;

/// <summary>Turns Ledger domain events into the public <c>ledger.*</c> integration events.</summary>
public static class LedgerIntegrationEvents
{
    /// <summary>The namespace holding the Ledger contracts (for the event registry).</summary>
    public static string ContractsNamespace => typeof(LedgerContracts.JournalPosted).Namespace!;

    public static IntegrationEvent? FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent is not JournalPosted { Journal: var journal })
        {
            return null;
        }

        return new LedgerContracts.JournalPosted(
            journal.Id.Value,
            journal.Type.ToString(),
            journal.Description,
            journal.Reference,
            journal.Amount,
            journal.Currency,
            journal.PostedAt,
            [.. journal.Entries.Select(entry => new LedgerContracts.JournalPostedEntry(
                entry.AccountId.Value, entry.Direction.ToString(), entry.Amount, entry.BalanceAfter))])
        {
            EventId = domainEvent.EventId,
            OccurredAt = domainEvent.OccurredAt,
        };
    }
}
