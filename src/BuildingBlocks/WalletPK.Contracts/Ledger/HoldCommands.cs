using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Holds driven by Sagas (M6): other services ask the Ledger to reserve, settle or return a customer's
// money by sending commands on "ledger.commands"; the Ledger answers on "ledger.replies". Everything is
// keyed by the hold ID, which the caller chooses (normally its Saga ID), so a command sent twice (after
// a timeout or a crash) finds the same hold and gets the same answer again.

/// <summary>Reserve money in a customer's wallet. Answered by <see cref="HoldPlaced"/> or <see cref="HoldRejected"/>.</summary>
/// <param name="HoldId">Chosen by the caller; the same ID again returns the same hold.</param>
/// <param name="UserId">Whose customer wallet.</param>
/// <param name="AmountPaisa">How much to reserve (fees included).</param>
/// <param name="Purpose">The journal type the hold will be captured as, e.g. "BankTransfer"; customer debits count against limits.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Reference">Business reference shown on the statement, e.g. the transfer ID.</param>
/// <param name="LifetimeSeconds">Returned automatically after this long if nobody settles it (60 s to 7 days).</param>
/// <param name="DeviceId">The device the customer used (for limits and fraud).</param>
[IntegrationEvent("ledger.place_hold", KafkaTopics.LedgerCommands)]
public sealed record PlaceHold(
    Guid HoldId,
    Guid UserId,
    long AmountPaisa,
    string Purpose,
    string Description,
    string? Reference,
    int LifetimeSeconds,
    string? DeviceId) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>One destination of a captured hold.</summary>
/// <param name="SystemAccount">A system account code, e.g. "BANK_FLOAT" (money leaving through a bank) or "FEE_INCOME".</param>
/// <param name="AmountPaisa">How much it receives.</param>
public sealed record HoldCredit(string SystemAccount, long AmountPaisa);

/// <summary>
/// Settle a hold: debit the wallet and credit the destinations, in one journal. Answered by
/// <see cref="HoldCaptured"/> or <see cref="HoldCaptureFailed"/>.
/// </summary>
/// <param name="HoldId">The hold.</param>
/// <param name="TransactionId">The journal to post; the same ID again posts nothing new.</param>
/// <param name="Purpose">The journal type, e.g. "BankTransfer".</param>
/// <param name="Description">Journal description (shown on the statement).</param>
/// <param name="Credits">Where the money goes; together at most the held amount (the rest is returned).</param>
[IntegrationEvent("ledger.capture_hold", KafkaTopics.LedgerCommands)]
public sealed record CaptureHold(Guid HoldId, Guid TransactionId, string Purpose, string Description, IReadOnlyList<HoldCredit> Credits) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>Return a hold's money to the wallet. Answered by <see cref="HoldReleased"/> (also when it was already returned).</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Reason">Why, for the audit trail.</param>
[IntegrationEvent("ledger.release_hold", KafkaTopics.LedgerCommands)]
public sealed record ReleaseHold(Guid HoldId, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>The money is reserved.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="WalletId">The wallet it is in.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="ExpiresAt">When it is returned automatically if nobody settles it.</param>
[IntegrationEvent("ledger.hold_placed", KafkaTopics.LedgerReplies)]
public sealed record HoldPlaced(Guid HoldId, Guid WalletId, long AmountPaisa, DateTimeOffset ExpiresAt) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>The money could not be reserved (nothing was).</summary>
/// <param name="HoldId">The hold that was asked for.</param>
/// <param name="Code">For example "ledger.insufficient_funds", "wallet.limit_exceeded", "wallet.not_found".</param>
/// <param name="Reason">Customer-facing explanation.</param>
[IntegrationEvent("ledger.hold_rejected", KafkaTopics.LedgerReplies)]
public sealed record HoldRejected(Guid HoldId, string Code, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>The hold is settled: the money left the wallet in journal <paramref name="TransactionId"/>.</summary>
[IntegrationEvent("ledger.hold_captured", KafkaTopics.LedgerReplies)]
public sealed record HoldCaptured(Guid HoldId, Guid TransactionId, long AmountPaisa) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>
/// The hold could not be settled, for example because it expired first. The money is back in the
/// wallet, but the payment may have gone out: this needs a person (or reconciliation) to look at it.
/// </summary>
[IntegrationEvent("ledger.hold_capture_failed", KafkaTopics.LedgerReplies)]
public sealed record HoldCaptureFailed(Guid HoldId, Guid TransactionId, string Code, string Reason) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}

/// <summary>
/// The hold's money is back in the wallet: it was released now or earlier, it expired, or it was never
/// placed (a rejected place). Releasing is safe to repeat.
/// </summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Status">"Released", "Expired" or "NotPlaced".</param>
[IntegrationEvent("ledger.hold_released", KafkaTopics.LedgerReplies)]
public sealed record HoldReleased(Guid HoldId, string Status) : IntegrationEvent
{
    public override string GetPartitionKey() => HoldId.ToString();
}
