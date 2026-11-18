using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Wallet-to-wallet transfers driven by Payments (M6.1): one command, one atomic journal in the Ledger
// (sender debited amount + fee + tax, recipient credited, fee and tax booked), one answer. The transfer
// ID is the journal ID, so the same command again gets the same answer and never moves money twice;
// a refusal is remembered too, so a late repeat cannot move money after the sender was told it failed.

/// <summary>Move money between two customer wallets. Answered by <see cref="FundsTransferred"/> or <see cref="FundsTransferRejected"/>.</summary>
/// <param name="TransferId">Chosen by the caller; becomes the journal ID.</param>
/// <param name="ReplyTo">The Saga to answer (echoed in the reply; often the transfer ID itself).</param>
/// <param name="SenderUserId">Whose wallet pays.</param>
/// <param name="ToWalletNumber">The recipient's wallet number (mobile number, E.164).</param>
/// <param name="AmountPaisa">What the recipient gets; the fee and tax are added on top.</param>
/// <param name="DeviceId">The sender's device (for limits and fraud).</param>
[IntegrationEvent("ledger.transfer_funds", KafkaTopics.LedgerCommands)]
public sealed record TransferFunds(
    Guid TransferId,
    Guid ReplyTo,
    Guid SenderUserId,
    string ToWalletNumber,
    long AmountPaisa,
    string? DeviceId) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}

/// <summary>The money moved.</summary>
/// <param name="TransferId">The transfer (journal) ID.</param>
/// <param name="ReplyTo">The Saga that asked.</param>
/// <param name="AmountPaisa">What the recipient got.</param>
/// <param name="FeePaisa">WalletPK's fee.</param>
/// <param name="TaxPaisa">Tax on the fee.</param>
/// <param name="ToWalletNumber">The recipient's wallet number, masked.</param>
/// <param name="PostedAt">When the journal was posted.</param>
[IntegrationEvent("ledger.funds_transferred", KafkaTopics.LedgerReplies)]
public sealed record FundsTransferred(
    Guid TransferId,
    Guid ReplyTo,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    string ToWalletNumber,
    DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}

/// <summary>The money did not move (insufficient balance, limit, unknown recipient ...).</summary>
/// <param name="TransferId">The transfer ID.</param>
/// <param name="ReplyTo">The Saga that asked.</param>
/// <param name="Code">Machine-readable reason, e.g. "wallet.insufficient_balance".</param>
/// <param name="Reason">For people.</param>
[IntegrationEvent("ledger.funds_transfer_rejected", KafkaTopics.LedgerReplies)]
public sealed record FundsTransferRejected(Guid TransferId, Guid ReplyTo, string Code, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}
