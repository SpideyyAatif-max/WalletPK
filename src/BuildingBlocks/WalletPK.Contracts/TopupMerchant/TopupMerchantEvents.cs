using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.TopupMerchant;

/// <summary>A mobile top-up finished, or needs a person (M8.1). Keyed by the customer.</summary>
/// <remarks>TelcoReference: the network's reference once it has one (M12: matched against its settlement file).</remarks>
[IntegrationEvent("topup.topup_changed", KafkaTopics.TopupEvents)]
public sealed record TopUpChanged(
    Guid TopUpId,
    Guid UserId,
    string Network,
    string Msisdn,
    string? BundleCode,
    long AmountPaisa,
    string Status,
    string Step,
    string Detail,
    DateTimeOffset ChangedAt,
    string? TelcoReference = null) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>A top-up the network never confirmed or denied, on the dead-letter topic topup.dlq, for operations.</summary>
[IntegrationEvent("topup.topup_dead_lettered", KafkaTopics.TopupDeadLetter)]
public sealed record TopUpDeadLettered(Guid TopUpId, Guid UserId, string Network, string Msisdn, long AmountPaisa, string LastState, string Reason, DateTimeOffset DeadLetteredAt) : IntegrationEvent
{
    public override string GetPartitionKey() => TopUpId.ToString();
}

/// <summary>A QR payment to a merchant finished (M8.4). Keyed by the merchant, so its till sees payments in order.</summary>
[IntegrationEvent("topup.merchant_payment_changed", KafkaTopics.TopupEvents)]
public sealed record MerchantPaymentChanged(
    Guid PaymentId,
    Guid MerchantId,
    Guid PayerUserId,
    Guid QrId,
    string Status,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long NetPaisa,
    string? Reference,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => MerchantId.ToString();
}

/// <summary>A merchant's sales were swept to its bank (M8.5).</summary>
[IntegrationEvent("topup.merchant_settled", KafkaTopics.TopupEvents)]
public sealed record MerchantSettlementChanged(Guid SettlementId, Guid MerchantId, DateOnly BusinessDate, int PaymentCount, long NetPaisa, string Status, DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => MerchantId.ToString();
}
