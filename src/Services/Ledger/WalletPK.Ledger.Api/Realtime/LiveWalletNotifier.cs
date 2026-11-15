using Microsoft.AspNetCore.SignalR;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Contracts.Ledger;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Ledger.Api.Realtime;

/// <summary>
/// Turns each <c>ledger.wallet_entry_posted</c> into a push to the wallet owner's open browsers.
/// Best effort by design: a browser that was offline reloads the balance and history over HTTP
/// when it reconnects, and the sequence tells it whether a push is newer than what it shows.
/// </summary>
internal sealed class LiveWalletNotifier(JsonEventSerializer serializer, IHubContext<WalletHub, IWalletClient> hub) : IEnvelopeProcessor
{
    public Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!serializer.TryUnwrap(envelope, out var integrationEvent) || integrationEvent is not WalletEntryPosted { OwnerId: { } ownerId } entry)
        {
            return Task.CompletedTask;
        }

        var type = Enum.Parse<JournalType>(entry.JournalType);
        var direction = Enum.Parse<EntryDirection>(entry.Direction);
        var balance = Money.FromPaisa(entry.BalanceAfterPaisa);
        var line = new StatementLine(
            entry.Sequence,
            new TransactionId(entry.TransactionId),
            type,
            StatementTitles.For(type, entry.Description, direction, entry.CounterpartyWalletNumber),
            direction == EntryDirection.Debit ? "Out" : "In",
            Money.FromPaisa(entry.AmountPaisa),
            balance,
            entry.PostedAt);

        return hub.Clients.User(ownerId.ToString())
            .EntryPosted(new LiveWalletEntry(new(entry.WalletId), entry.WalletType, entry.Sequence, balance, line));
    }
}
