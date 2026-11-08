namespace WalletPK.Common.Domain;

/// <summary>
/// The entry point to a cluster of entities that change together in one transaction
/// (for example a Wallet with its holds). It records domain events, which are saved
/// to the outbox in the same transaction and published afterwards.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>For EF Core materialisation only.</summary>
    protected AggregateRoot()
    {
    }

    /// <summary>Events raised since the aggregate was loaded or last cleared.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Removes recorded events once they have been saved to the outbox.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
