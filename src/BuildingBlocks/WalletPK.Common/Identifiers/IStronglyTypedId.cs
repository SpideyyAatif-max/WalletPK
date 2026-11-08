namespace WalletPK.Common.Identifiers;

/// <summary>
/// A Guid wrapped in its own type, so a <c>UserId</c> can never be passed where a
/// <c>WalletId</c> is expected. New values are UUIDv7: time-ordered, which keeps
/// database index inserts fast.
/// </summary>
public interface IStronglyTypedId<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}
