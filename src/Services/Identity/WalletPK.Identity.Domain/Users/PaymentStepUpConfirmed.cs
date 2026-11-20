using WalletPK.Common.Domain;
using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// The user confirmed, with a one-time code sent to their phone, that they are making a payment the fraud
/// engine asked to step up. It covers one payment from <paramref name="DeviceId"/> until <paramref name="ValidUntil"/>.
/// </summary>
public sealed record PaymentStepUpConfirmed(UserId UserId, Guid? DeviceId, DateTimeOffset ConfirmedAt, DateTimeOffset ValidUntil) : DomainEvent(ConfirmedAt);
