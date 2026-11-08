using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Users;

/// <summary>Raised when a new user completes registration (verified phone + MPIN set).</summary>
public sealed record UserRegistered(UserId UserId, string PhoneNumber, DateTimeOffset RegisteredAt) : DomainEvent(RegisteredAt);
