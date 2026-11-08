namespace WalletPK.Common.Domain;

/// <summary>An object with an identity that stays the same while its data changes.</summary>
public abstract class Entity<TId>
    where TId : struct
{
    protected Entity(TId id)
    {
        Id = id;
    }

    /// <summary>For EF Core materialisation only.</summary>
    protected Entity()
    {
    }

    public TId Id { get; protected set; }
}
