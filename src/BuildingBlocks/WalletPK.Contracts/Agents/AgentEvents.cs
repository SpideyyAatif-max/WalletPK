using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Agents;

/// <summary>An agent was approved, opened, suspended or reactivated (M9.1). Keyed by the agent.</summary>
[IntegrationEvent("agents.agent_status_changed", KafkaTopics.AgentsEvents)]
public sealed record AgentStatusChanged(Guid AgentId, Guid OwnerUserId, string AgentCode, string Status, string? Reason, DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}

/// <summary>
/// A cash-in, cash-out or float move finished (M9.1-M9.3). Kind is "CashIn", "CashOut", "FloatIn" or
/// "FloatOut"; CustomerUserId is set for cash-ins and cash-outs. Keyed by the agent, so its till sees them in order.
/// </summary>
[IntegrationEvent("agents.transaction_changed", KafkaTopics.AgentsEvents)]
public sealed record AgentTransactionChanged(
    Guid TransactionId,
    Guid AgentId,
    Guid? CustomerUserId,
    string Kind,
    string Status,
    long AmountPaisa,
    long FeePaisa,
    long CommissionPaisa,
    string? FailureCode,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}

/// <summary>An agent's float went below its alert level after a cash-in (M9.1): time to load more.</summary>
[IntegrationEvent("agents.float_low", KafkaTopics.AgentsEvents)]
public sealed record AgentFloatLow(Guid AgentId, Guid OwnerUserId, long FloatPaisa, long ThresholdPaisa, DateTimeOffset At) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}

/// <summary>An agent's accrued commissions were paid to its float, or the payout was refused (M9.4).</summary>
[IntegrationEvent("agents.commission_payout_changed", KafkaTopics.AgentsEvents)]
public sealed record AgentCommissionPayoutChanged(
    Guid PayoutId,
    Guid AgentId,
    DateOnly BusinessDate,
    int TransactionCount,
    long GrossPaisa,
    long TaxPaisa,
    long NetPaisa,
    string Status,
    DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => AgentId.ToString();
}
