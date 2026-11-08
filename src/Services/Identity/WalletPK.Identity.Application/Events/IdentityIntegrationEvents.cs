using WalletPK.Common.Domain;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;
using WalletPK.Messaging.Events;
using IdentityContracts = WalletPK.Contracts.Identity;

namespace WalletPK.Identity.Application.Events;

/// <summary>
/// Decides which internal domain events become public integration events, and what they contain.
/// Domain events can change freely; integration events are a contract other services rely on.
/// <list type="bullet">
/// <item><see cref="UserRegistered"/> → <c>identity.user_registered</c></item>
/// <item><see cref="DeviceTrusted"/> → <c>identity.device_bound</c></item>
/// <item><see cref="KycUpgraded"/> → <c>identity.kyc_upgraded</c></item>
/// <item>Lockouts and device removals stay internal for now (Fraud may subscribe in M10).</item>
/// </list>
/// The integration event reuses the domain event's ID and time, so it can be traced back to it.
/// </summary>
public static class IdentityIntegrationEvents
{
    /// <summary>The namespace holding the Identity contracts (for the event registry).</summary>
    public static string ContractsNamespace => typeof(IdentityContracts.UserRegistered).Namespace!;

    public static IntegrationEvent? FromDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        IntegrationEvent? integrationEvent = domainEvent switch
        {
            UserRegistered registered => new IdentityContracts.UserRegistered(
                registered.UserId.Value, registered.PhoneNumber, KycTier.L0.ToString(), registered.RegisteredAt),
            DeviceTrusted trusted => new IdentityContracts.DeviceBound(
                trusted.UserId.Value, trusted.DeviceId.Value, trusted.CoolingOffUntil, trusted.TrustedAt),
            KycUpgraded upgraded => new IdentityContracts.KycUpgraded(
                upgraded.UserId.Value, upgraded.PreviousTier.ToString(), upgraded.NewTier.ToString(), upgraded.UpgradedAt),
            _ => null,
        };

        return integrationEvent is null
            ? null
            : integrationEvent with { EventId = domainEvent.EventId, OccurredAt = domainEvent.OccurredAt };
    }
}
