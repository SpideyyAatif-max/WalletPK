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

/// <summary>
/// One entry on a customer, merchant or agent wallet (M4.2): the wallet's view of a journal. Keyed by
/// the wallet ID, so all of one wallet's events land on one partition and are consumed in order; and
/// <see cref="Sequence"/> numbers them 1, 2, 3 ... per wallet with no gaps, so a consumer can tell a
/// duplicate (sequence already seen) from a gap (sequence skipped). Read models, live balances and
/// notifications build on this. Amounts are whole paisa.
/// </summary>
/// <param name="WalletId">The wallet's account ID (also the Kafka key).</param>
/// <param name="OwnerId">The wallet's owner.</param>
/// <param name="WalletNumber">The wallet's mobile number (E.164).</param>
/// <param name="WalletType">"CustomerWallet", "MerchantWallet" or "AgentWallet".</param>
/// <param name="Sequence">This entry's number in the wallet's history.</param>
/// <param name="TransactionId">The journal it belongs to.</param>
/// <param name="JournalType">For example "P2PTransfer" or "CashIn".</param>
/// <param name="Description">The journal's description.</param>
/// <param name="Direction">"Debit" (money out of the wallet) or "Credit" (money in).</param>
/// <param name="AmountPaisa">Always positive.</param>
/// <param name="BalanceAfterPaisa">The wallet's balance right after this entry.</param>
/// <param name="CounterpartyWalletNumber">The other wallet in a wallet-to-wallet payment, if there is exactly one.</param>
/// <param name="PostedAt">When the journal was posted.</param>
[IntegrationEvent("ledger.wallet_entry_posted", KafkaTopics.LedgerEvents)]
public sealed record WalletEntryPosted(
    Guid WalletId,
    Guid? OwnerId,
    string? WalletNumber,
    string WalletType,
    long Sequence,
    Guid TransactionId,
    string JournalType,
    string Description,
    string Direction,
    long AmountPaisa,
    long BalanceAfterPaisa,
    string? CounterpartyWalletNumber,
    DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => WalletId.ToString();
}
