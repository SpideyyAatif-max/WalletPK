using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Users;

/// <summary>Raised when a user's KYC tier goes up (and with it their transaction limits).</summary>
public sealed record KycUpgraded(UserId UserId, KycTier PreviousTier, KycTier NewTier, DateTimeOffset UpgradedAt) : DomainEvent(UpgradedAt);
