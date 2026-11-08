namespace WalletPK.Common.Domain;

/// <summary>Something meaningful that happened in the domain, for example "MoneyTransferred".</summary>
public interface IDomainEvent
{
    /// <summary>Unique ID, used by consumers to process each event exactly once.</summary>
    Guid EventId { get; }

    /// <summary>When it happened (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
