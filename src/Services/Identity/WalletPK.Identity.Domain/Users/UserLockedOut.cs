using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Users;

/// <summary>Raised when too many wrong MPINs lock an account (a fraud signal for M10).</summary>
public sealed record UserLockedOut(UserId UserId, DateTimeOffset LockedAt, DateTimeOffset LockedUntil) : DomainEvent(LockedAt);
