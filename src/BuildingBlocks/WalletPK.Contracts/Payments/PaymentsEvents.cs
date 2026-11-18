using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Payments;

// Integration events published by the Payments service on "payments.events" (M6).

/// <summary>
/// A bank transfer moved to a new step. Notifications (M11), the Ops console and the live tracker
/// follow these; keyed by the user so one user's updates stay in order.
/// </summary>
/// <param name="TransferId">The transfer (also its Saga ID).</param>
/// <param name="UserId">Who sent it.</param>
/// <param name="Status">Customer-facing status: Processing, Queued, Completed or Failed.</param>
/// <param name="Step">The Saga state, e.g. "AwaitingBank".</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="ToBank">Beneficiary bank code, e.g. "HBL".</param>
/// <param name="Detail">What just happened, in words.</param>
/// <param name="ChangedAt">When.</param>
[IntegrationEvent("payments.bank_transfer_changed", KafkaTopics.PaymentsEvents)]
public sealed record BankTransferChanged(
    Guid TransferId,
    Guid UserId,
    string Status,
    string Step,
    long AmountPaisa,
    string ToBank,
    string Detail,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>A wallet-to-wallet transfer finished (or was accepted). Keyed by the sender.</summary>
/// <param name="TransferId">The transfer (Saga and journal ID).</param>
/// <param name="SenderUserId">Who sent it.</param>
/// <param name="ToWalletNumber">The recipient's wallet number.</param>
/// <param name="Status">Processing, Completed or Failed.</param>
/// <param name="AmountPaisa">How much the recipient gets.</param>
/// <param name="Detail">What happened, in words.</param>
/// <param name="ChangedAt">When.</param>
[IntegrationEvent("payments.p2p_transfer_changed", KafkaTopics.PaymentsEvents)]
public sealed record P2PTransferChanged(
    Guid TransferId,
    Guid SenderUserId,
    string ToWalletNumber,
    string Status,
    long AmountPaisa,
    string Detail,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => SenderUserId.ToString();
}

/// <summary>A money request changed (created, paid, declined, cancelled, expired). Keyed by the request.</summary>
/// <param name="RequestId">The request (Saga ID).</param>
/// <param name="RequesterUserId">Who asked.</param>
/// <param name="RequesterWalletNumber">Who asked, by wallet number.</param>
/// <param name="PayerWalletNumber">Who is asked to pay.</param>
/// <param name="Status">Pending, Paying, Paid, Declined, Cancelled or Expired.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="SplitId">The split bill it belongs to, if any.</param>
/// <param name="Detail">What happened, in words.</param>
/// <param name="ChangedAt">When.</param>
[IntegrationEvent("payments.money_request_changed", KafkaTopics.PaymentsEvents)]
public sealed record MoneyRequestChanged(
    Guid RequestId,
    Guid RequesterUserId,
    string RequesterWalletNumber,
    string PayerWalletNumber,
    string Status,
    long AmountPaisa,
    Guid? SplitId,
    string Detail,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => RequestId.ToString();
}
