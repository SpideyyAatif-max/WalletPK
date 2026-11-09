using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WalletPK.Common.Domain;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Persistence.Outbox;

/// <summary>
/// Runs just before EF Core saves. It takes the domain events recorded by every changed aggregate,
/// turns the public ones into integration events (via the service's <see cref="IIntegrationEventMapper"/>)
/// and adds them as <see cref="OutboxMessage"/> rows, so they are committed in the <b>same transaction</b>
/// as the change itself. Either both are saved or neither is: an event can never be lost, or published
/// for a change that rolled back. No use case has to remember to publish anything.
/// </summary>
/// <remarks>The DbContext must map the outbox table with <see cref="OutboxModelBuilderExtensions.AddOutboxMessages"/>.</remarks>
public sealed class DomainEventOutboxInterceptor(
    IIntegrationEventMapper mapper,
    JsonEventSerializer serializer,
    TimeProvider timeProvider,
    Func<string?> correlationIdAccessor) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        MoveDomainEventsToOutbox(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        MoveDomainEventsToOutbox(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void MoveDomainEventsToOutbox(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var correlationId = correlationIdAccessor();
        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                if (mapper.Map(domainEvent) is { } integrationEvent)
                {
                    var envelope = serializer.Wrap(integrationEvent, correlationId);
                    context.Set<OutboxMessage>().Add(OutboxMessage.From(envelope, now));
                }
            }

            // The events now live in the outbox rows; if the save fails, the unit of work discards both.
            aggregate.ClearDomainEvents();
        }
    }
}
