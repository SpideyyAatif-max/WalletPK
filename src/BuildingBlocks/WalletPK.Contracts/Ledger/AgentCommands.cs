using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Ledger;

// Agents (M9), driven by the Agent Network service. Each command is answered on ledger.replies, keyed
// and idempotent by its ID; a refusal (FundsTransferRejected) is remembered like a transfer's.
//
// An agent's float is its agent wallet: e-money the agent holds. A cash-in moves float to the customer
// (the agent takes the cash), a cash-out moves the customer's money to the float (the agent hands over
// cash). Commissions are accrued in the same journal (COMMISSION_EXPENSE / COMMISSION_PAYABLE) and paid
// to the float later (PayAgentCommission).

/// <summary>Opens an agent's wallet (one per owner); answered by <see cref="AgentWalletOpened"/>, also when it already exists.</summary>
/// <param name="AgentId">The agent (echoed in the reply).</param>
/// <param name="OwnerUserId">The customer who runs the agent shop.</param>
/// <param name="AgentCode">The agent's wallet number ("WPKA123456").</param>
/// <param name="Name">The shop name.</param>
[IntegrationEvent("ledger.open_agent_wallet", KafkaTopics.LedgerCommands)]
public sealed record OpenAgentWallet(Guid AgentId, Guid OwnerUserId, string AgentCode, string Name) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}

[IntegrationEvent("ledger.agent_wallet_opened", KafkaTopics.LedgerReplies)]
public sealed record AgentWalletOpened(Guid AgentId, Guid WalletId, string AgentCode) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}

/// <summary>
/// Cash-in (M9.2): the agent takes cash and the customer's wallet is credited from the agent's float. The
/// customer pays nothing; the agent's commission is accrued in the same journal. Answered by
/// <see cref="AgentCashedIn"/> or <see cref="FundsTransferRejected"/>.
/// </summary>
/// <param name="TransactionId">Becomes the journal ID.</param>
/// <param name="ReplyTo">The Saga to answer.</param>
/// <param name="AgentOwnerUserId">Whose agent wallet pays.</param>
/// <param name="CustomerWalletNumber">The customer's wallet number (E.164).</param>
/// <param name="AmountPaisa">What the customer receives.</param>
/// <param name="CommissionPaisa">The agent's commission, accrued.</param>
/// <param name="Description">"Cash deposit at Bismillah Easy Cash".</param>
[IntegrationEvent("ledger.agent_cash_in", KafkaTopics.LedgerCommands)]
public sealed record AgentCashIn(
    Guid TransactionId,
    Guid ReplyTo,
    Guid AgentOwnerUserId,
    string CustomerWalletNumber,
    long AmountPaisa,
    long CommissionPaisa,
    string Description) : IntegrationEvent
{
    public override string GetPartitionKey() => TransactionId.ToString();
}

/// <summary>
/// The cash-in was posted: CustomerUserId's wallet was credited; CustomerName is that wallet's name (its
/// masked number); AgentBalancePaisa is the agent's float right after.
/// </summary>
[IntegrationEvent("ledger.agent_cashed_in", KafkaTopics.LedgerReplies)]
public sealed record AgentCashedIn(
    Guid TransactionId,
    Guid ReplyTo,
    Guid CustomerUserId,
    string CustomerName,
    long AmountPaisa,
    long CommissionPaisa,
    long AgentBalancePaisa,
    DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => TransactionId.ToString();
}

/// <summary>
/// Cash-out (M9.3): the customer is debited the amount plus the fee and its tax, the agent's float is
/// credited the amount (the agent hands over that much cash), FEE_INCOME and TAX_PAYABLE the rest; the
/// agent's commission is accrued in the same journal. Counts against the customer's limits. Answered by
/// <see cref="AgentCashedOut"/> or <see cref="FundsTransferRejected"/>.
/// </summary>
[IntegrationEvent("ledger.agent_cash_out", KafkaTopics.LedgerCommands)]
public sealed record AgentCashOut(
    Guid TransactionId,
    Guid ReplyTo,
    Guid AgentOwnerUserId,
    Guid CustomerUserId,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long CommissionPaisa,
    string Description,
    string? DeviceId) : IntegrationEvent
{
    public override string GetPartitionKey() => TransactionId.ToString();
}

[IntegrationEvent("ledger.agent_cashed_out", KafkaTopics.LedgerReplies)]
public sealed record AgentCashedOut(
    Guid TransactionId,
    Guid ReplyTo,
    long AmountPaisa,
    long FeePaisa,
    long TaxPaisa,
    long CommissionPaisa,
    long AgentBalancePaisa,
    DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => TransactionId.ToString();
}

/// <summary>
/// Moves money between an agent's own customer wallet and its float (M9.1): "ToFloat" to load the float,
/// "ToWallet" to take float back (from there to a bank, by Raast). Direction is "ToFloat" or "ToWallet".
/// Answered by <see cref="AgentFloatMoved"/> or <see cref="FundsTransferRejected"/>.
/// </summary>
[IntegrationEvent("ledger.move_agent_float", KafkaTopics.LedgerCommands)]
public sealed record MoveAgentFloat(Guid TransferId, Guid ReplyTo, Guid AgentOwnerUserId, string Direction, long AmountPaisa) : IntegrationEvent
{
    public const string ToFloat = "ToFloat";
    public const string ToWallet = "ToWallet";

    public override string GetPartitionKey() => TransferId.ToString();
}

[IntegrationEvent("ledger.agent_float_moved", KafkaTopics.LedgerReplies)]
public sealed record AgentFloatMoved(Guid TransferId, Guid ReplyTo, string Direction, long AmountPaisa, long AgentBalancePaisa, DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}

/// <summary>
/// Pays an agent's accrued commissions (M9.4): COMMISSION_PAYABLE is debited the gross, the agent's float
/// credited the gross minus withholding tax, TAX_PAYABLE the tax. Answered by
/// <see cref="AgentCommissionPaid"/> or <see cref="FundsTransferRejected"/> (with ReplyTo = AgentId).
/// </summary>
[IntegrationEvent("ledger.pay_agent_commission", KafkaTopics.LedgerCommands)]
public sealed record PayAgentCommission(Guid PayoutId, Guid AgentId, Guid AgentOwnerUserId, long GrossPaisa, long TaxPaisa, string Description) : IntegrationEvent
{
    public override string GetPartitionKey() => PayoutId.ToString();
}

[IntegrationEvent("ledger.agent_commission_paid", KafkaTopics.LedgerReplies)]
public sealed record AgentCommissionPaid(Guid PayoutId, Guid AgentId, long GrossPaisa, long TaxPaisa, long NetPaisa, DateTimeOffset PostedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => PayoutId.ToString();
}
