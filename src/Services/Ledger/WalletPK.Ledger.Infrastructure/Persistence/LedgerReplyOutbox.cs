using WalletPK.Ledger.Application.Holds;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Ledger.Infrastructure.Persistence;

/// <summary>Replies go into the Ledger's outbox; the inbox processor's SaveChanges commits them with the change.</summary>
internal sealed class LedgerReplyOutbox(LedgerDbContext db, JsonEventSerializer serializer, TimeProvider timeProvider) : IReplyOutbox
{
    public void Send(IntegrationEvent reply, EventContext answering)
    {
        ArgumentNullException.ThrowIfNull(answering);
        db.AddToOutbox(serializer, reply, timeProvider.GetUtcNow(), answering.CorrelationId, answering.EventId.ToString());
    }
}
