using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Bills;

/// <summary>A bill payment's customer-visible status changed (M7). Keyed by the customer.</summary>
/// <param name="PaymentId">The payment (Saga ID).</param>
/// <param name="UserId">Who pays.</param>
/// <param name="Biller">The biller's code.</param>
/// <param name="ConsumerNumber">Whose bill.</param>
/// <param name="Status">Processing, PendingVerification, UnderReview, Completed or Failed.</param>
/// <param name="Step">The Saga's state.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="Detail">What happened, in words.</param>
/// <param name="ChangedAt">When.</param>
[IntegrationEvent("bills.payment_changed", KafkaTopics.BillsEvents)]
public sealed record BillPaymentChanged(
    Guid PaymentId,
    Guid UserId,
    string Biller,
    string ConsumerNumber,
    string Status,
    string Step,
    long AmountPaisa,
    string Detail,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>
/// A bill payment that needs a person (M7.5), on the dead-letter topic bills.dlq: the operations
/// back-office lists these; the fix is a replay, a refund or a confirmation through the Bills API.
/// </summary>
/// <param name="PaymentId">The payment.</param>
/// <param name="UserId">Whose money is reserved.</param>
/// <param name="Biller">The biller.</param>
/// <param name="ConsumerNumber">Whose bill.</param>
/// <param name="AmountPaisa">How much is reserved.</param>
/// <param name="LastState">Where it got stuck.</param>
/// <param name="Reason">Why.</param>
/// <param name="DeadLetteredAt">When.</param>
[IntegrationEvent("bills.payment_dead_lettered", KafkaTopics.BillsDeadLetter)]
public sealed record BillPaymentDeadLettered(
    Guid PaymentId,
    Guid UserId,
    string Biller,
    string ConsumerNumber,
    long AmountPaisa,
    string LastState,
    string Reason,
    DateTimeOffset DeadLetteredAt) : IntegrationEvent
{
    public override string GetPartitionKey() => PaymentId.ToString();
}

/// <summary>A saved bill is due soon and unpaid (M7.6); the notifications service turns it into a push or SMS.</summary>
/// <param name="UserId">Whom to remind.</param>
/// <param name="SavedBillerId">Which saved biller.</param>
/// <param name="Biller">The biller.</param>
/// <param name="ConsumerNumber">Whose bill.</param>
/// <param name="Nickname">The customer's name for it ("Home electricity").</param>
/// <param name="BillingMonth">"2026-11".</param>
/// <param name="DueDate">Pay by.</param>
/// <param name="AmountPaisa">Payable now.</param>
[IntegrationEvent("bills.bill_due_reminder", KafkaTopics.BillsEvents)]
public sealed record BillDueReminder(
    Guid UserId,
    Guid SavedBillerId,
    string Biller,
    string ConsumerNumber,
    string? Nickname,
    string BillingMonth,
    DateOnly DueDate,
    long AmountPaisa) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}
