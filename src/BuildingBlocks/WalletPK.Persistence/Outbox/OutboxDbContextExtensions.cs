using Microsoft.EntityFrameworkCore;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Persistence.Outbox;

public static class OutboxDbContextExtensions
{
    /// <summary>
    /// Queues an integration event in the outbox of <paramref name="db"/>: it is published if, and only
    /// if, the current unit of work commits. For events that are not domain events, such as a Saga's
    /// commands or a service's replies to them.
    /// </summary>
    public static void AddToOutbox(
        this DbContext db,
        JsonEventSerializer serializer,
        IntegrationEvent integrationEvent,
        DateTimeOffset now,
        string? correlationId,
        string? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(integrationEvent);
        db.Set<OutboxMessage>().Add(OutboxMessage.From(serializer.Wrap(integrationEvent, correlationId, causationId), now));
    }
}
