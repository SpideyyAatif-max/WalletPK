using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Identity;

// Integration events published by the Identity service on the "identity.events" topic.
// Every event is keyed by the user ID, so one user's events are consumed in order.

/// <summary>A new account was created. The Ledger opens the user's wallet when it sees this (M4).</summary>
/// <param name="UserId">The new user.</param>
/// <param name="PhoneNumber">E.164 mobile number; it is also the wallet number customers send money to.</param>
/// <param name="KycTier">Starting KYC tier, always "L0".</param>
/// <param name="RegisteredAt">When the account was created.</param>
[IntegrationEvent("identity.user_registered", KafkaTopics.IdentityEvents)]
public sealed record UserRegistered(Guid UserId, string PhoneNumber, string KycTier, DateTimeOffset RegisteredAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>A device became trusted after OTP verification (new phone, or a removed phone added back).</summary>
/// <param name="UserId">The device's owner.</param>
/// <param name="DeviceId">The trusted device.</param>
/// <param name="CoolingOffUntil">End of the new-device cooling-off period, if any (Fraud watches these devices closely).</param>
/// <param name="BoundAt">When the device was trusted.</param>
[IntegrationEvent("identity.device_bound", KafkaTopics.IdentityEvents)]
public sealed record DeviceBound(Guid UserId, Guid DeviceId, DateTimeOffset? CoolingOffUntil, DateTimeOffset BoundAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>The user's KYC tier went up, so their limits did too.</summary>
/// <param name="UserId">The upgraded user.</param>
/// <param name="PreviousTier">For example "L0".</param>
/// <param name="NewTier">For example "L1".</param>
/// <param name="UpgradedAt">When the upgrade happened.</param>
[IntegrationEvent("identity.kyc_upgraded", KafkaTopics.IdentityEvents)]
public sealed record KycUpgraded(Guid UserId, string PreviousTier, string NewTier, DateTimeOffset UpgradedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}
