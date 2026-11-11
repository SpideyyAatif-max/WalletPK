using WalletPK.Common.Domain;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Messaging.Events;
using LedgerContracts = WalletPK.Contracts.Ledger;

namespace WalletPK.Ledger.Application.Events;

/// <summary>Turns Ledger domain events into the public <c>ledger.*</c> integration events.</summary>
public static class LedgerIntegrationEvents
{
    /// <summary>The namespace holding the Ledger contracts (for the event registry).</summary>
    public static string ContractsNamespace => typeof(LedgerContracts.JournalPosted).Namespace!;

    /// <summary>
    /// A posted journal becomes one <c>ledger.journal_posted</c> (keyed by the journal) plus one
    /// <c>ledger.wallet_entry_posted</c> per wallet entry (keyed by the wallet); an opened wallet becomes
    /// <c>ledger.wallet_opened</c> (keyed by the wallet). Other domain events stay internal.
    /// </summary>
    public static IReadOnlyList<IntegrationEvent> FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent is WalletOpened opened)
        {
            return
            [
                new LedgerContracts.WalletOpened(opened.WalletId.Value, opened.OwnerId.Value, opened.Alias, opened.Type.ToString(), opened.OpenedAt)
                {
                    EventId = domainEvent.EventId,
                    OccurredAt = domainEvent.OccurredAt,
                },
            ];
        }

        if (domainEvent is not JournalPosted { Journal: var journal, Accounts: var accounts })
        {
            return [];
        }

        List<IntegrationEvent> events =
        [
            new LedgerContracts.JournalPosted(
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
            },
        ];

        var byId = accounts.ToDictionary(account => account.Id);
        var walletEntries = journal.Entries.Where(entry => byId.TryGetValue(entry.AccountId, out var account) && account.IsWallet).ToList();

        foreach (var entry in walletEntries)
        {
            var wallet = byId[entry.AccountId];

            // "Sent to X" / "Received from X": the one other wallet on the opposite side, if there is exactly one.
            var counterparties = walletEntries
                .Where(other => other.AccountId != entry.AccountId && other.Direction != entry.Direction)
                .Select(other => byId[other.AccountId].Alias)
                .Distinct()
                .ToList();

            events.Add(new LedgerContracts.WalletEntryPosted(
                wallet.Id.Value,
                wallet.OwnerId?.Value,
                wallet.Alias,
                wallet.Type.ToString(),
                entry.AccountSequence,
                journal.Id.Value,
                journal.Type.ToString(),
                journal.Description,
                entry.Direction.ToString(),
                entry.Amount,
                entry.BalanceAfter,
                counterparties.Count == 1 ? counterparties[0] : null,
                journal.PostedAt)
            {
                OccurredAt = domainEvent.OccurredAt,
            });
        }

        return events;
    }
}
