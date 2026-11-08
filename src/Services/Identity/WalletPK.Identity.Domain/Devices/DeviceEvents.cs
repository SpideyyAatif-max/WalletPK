using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Devices;

/// <summary>Raised when a device becomes trusted (new, or trusted again after being revoked).</summary>
public sealed record DeviceTrusted(DeviceId DeviceId, UserId UserId, DateTimeOffset TrustedAt, DateTimeOffset? CoolingOffUntil)
    : DomainEvent(TrustedAt);

/// <summary>Raised when a device is removed from the user's trusted devices.</summary>
public sealed record DeviceRevoked(DeviceId DeviceId, UserId UserId, DateTimeOffset RevokedAt) : DomainEvent(RevokedAt);
