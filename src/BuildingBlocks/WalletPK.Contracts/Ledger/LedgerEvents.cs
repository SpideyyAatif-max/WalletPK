using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Integration events published by the Ledger service on the "ledger.events" topic.

/// <summary>
/// A journal was posted: money moved between accounts. Read models (M4), fraud (M10) and
/// notifications (M11) build on this. Amounts are whole paisa.
/// </summary>
/// <param name="TransactionId">The journal's ID (also the Kafka key).</param>
/// <param name="JournalType">For example "P2PTransfer" or "CashIn".</param>
/// <param name="Description">What the journal records.</param>
/// <param name="Reference">The business reference, for example a payment ID.</param>
/// <param name="AmountPaisa">Total moved (sum of the debits).</param>
/// <param name="Currency">Always "PKR" for now.</param>
/// <param name="PostedAt">When it was posted.</param>
/// <param name="Entries">Every debit and credit, with each account's balance right after it.</param>
[IntegrationEvent("ledger.journal_posted", KafkaTopics.LedgerEvents)]
public sealed record JournalPosted(
    Guid TransactionId,
    string JournalType,
    string Description,
    string? Reference,
    long AmountPaisa,
    string Currency,
    DateTimeOffset PostedAt,
    IReadOnlyList<JournalPostedEntry> Entries) : IntegrationEvent
{
    public override string GetPartitionKey() => TransactionId.ToString();
}

/// <param name="AccountId">The account.</param>
/// <param name="Direction">"Debit" or "Credit".</param>
/// <param name="AmountPaisa">Always positive.</param>
/// <param name="BalanceAfterPaisa">The account's balance right after this entry.</param>
public sealed record JournalPostedEntry(Guid AccountId, string Direction, long AmountPaisa, long BalanceAfterPaisa);
