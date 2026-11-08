namespace WalletPK.Common.Domain;

/// <summary>
/// Anything that records domain events (every <see cref="AggregateRoot{TId}"/>). Non-generic, so
/// persistence code can collect the events of all changed aggregates in one pass and move them
/// to the outbox, whatever their ID type.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
