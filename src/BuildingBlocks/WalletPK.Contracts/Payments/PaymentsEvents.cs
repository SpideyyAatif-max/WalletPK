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
