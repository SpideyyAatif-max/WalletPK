using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Merchants (M8), driven by the Top-up & Merchant service. Each command is answered on ledger.replies,
// keyed and idempotent by its ID; a refusal (FundsTransferRejected) is remembered like a transfer's.

/// <summary>Opens a merchant's wallet (one per owner); answered by <see cref="MerchantWalletOpened"/>, also when it already exists.</summary>
/// <param name="MerchantId">The merchant (echoed in the reply).</param>
/// <param name="OwnerUserId">The customer who runs the business.</param>
/// <param name="MerchantCode">The merchant's wallet number ("WPKM123456").</param>
/// <param name="Name">The business name.</param>
[IntegrationEvent("ledger.open_merchant_wallet", KafkaTopics.LedgerCommands)]
public sealed record OpenMerchantWallet(Guid MerchantId, Guid OwnerUserId, string MerchantCode, string Name) : IntegrationEvent
{
    public override string GetPartitionKey() => MerchantId.ToString();
}

[IntegrationEvent("ledger.merchant_wallet_opened", KafkaTopics.LedgerReplies)]
public sealed record MerchantWalletOpened(Guid MerchantId, Guid WalletId, string MerchantCode) : IntegrationEvent
{
    public override string GetPartitionKey() => MerchantId.ToString();
}

/// <summary>
/// A customer pays a merchant (QR payment, M8.4), in one journal: the customer is debited the amount, the
/// merchant credited the amount minus its fee and the tax on it, FEE_INCOME and TAX_PAYABLE credited the
/// rest. Answered by <see cref="MerchantPaid"/> or <see cref="FundsTransferRejected"/>.
/// </summary>
/// <param name="PaymentId">Becomes the journal ID.</param>
/// <param name="ReplyTo">The Saga to answer.</param>
/// <param name="PayerUserId">Who pays.</param>
/// <param name="MerchantOwnerUserId">Whose merchant wallet is paid.</param>
/// <param name="AmountPaisa">What the customer pays.</param>
/// <param name="FeePaisa">The merchant's fee (from its fee plan).</param>
/// <param name="TaxPaisa">Tax on the fee.</param>
/// <param name="Description">"QR payment to Ali Tea Stall (INV-42)".</param>
/// <param name="DeviceId">The payer's device (limits).</param>
[IntegrationEvent("ledger.pay_merchant", KafkaTopics.LedgerCommands)]
public sealed record PayMerchant(
    Guid PaymentId,
    Guid ReplyTo,
    Guid PayerUserId,
    Guid MerchantOwnerUserId,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    string Description,
    string? DeviceId) : IntegrationEvent
{
    public override string GetPartitionKey() => PaymentId.ToString();
}

[IntegrationEvent("ledger.merchant_paid", KafkaTopics.LedgerReplies)]
public sealed record MerchantPaid(Guid PaymentId, Guid ReplyTo, long AmountPaisa, long FeePaisa, long TaxPaisa, long NetPaisa, DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => PaymentId.ToString();
}

/// <summary>
/// Moves a day's net sales from a merchant's wallet to its bank (nightly settlement, M8.5): debit the
/// merchant wallet, credit BANK_FLOAT. Answered by <see cref="MerchantSettled"/> or <see cref="FundsTransferRejected"/>.
/// </summary>
/// <param name="SettlementId">Becomes the journal ID (one per merchant and business day).</param>
/// <param name="MerchantId">Echoed in the reply.</param>
/// <param name="MerchantOwnerUserId">Whose merchant wallet.</param>
/// <param name="AmountPaisa">The day's net sales.</param>
/// <param name="Description">"Settlement 2026-11-18".</param>
[IntegrationEvent("ledger.settle_merchant", KafkaTopics.LedgerCommands)]
public sealed record SettleMerchant(Guid SettlementId, Guid MerchantId, Guid MerchantOwnerUserId, long AmountPaisa, string Description) : IntegrationEvent
{
    public override string GetPartitionKey() => SettlementId.ToString();
}

[IntegrationEvent("ledger.merchant_settled", KafkaTopics.LedgerReplies)]
public sealed record MerchantSettled(Guid SettlementId, Guid MerchantId, long AmountPaisa, DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => SettlementId.ToString();
}
