using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Contracts.Fraud;

// Events published by the Fraud service on the "fraud.alerts" topic (M10.6): compliance dashboards and
// notifications (M11) build on them. Keyed by the user, so one user's alerts arrive in order.

/// <summary>
/// A risky payment or pattern: a payment refused for review or blocked, or a mule ring. Severity is "Low",
/// "Medium" or "High"; Action is what was done ("Allow", "Review", "Block", "Freeze").
/// </summary>
[IntegrationEvent("fraud.alert_raised", KafkaTopics.FraudAlerts)]
public sealed record FraudAlertRaised(
    Guid AlertId,
    Guid UserId,
    Guid? TransactionId,
    string Kind,
    string Severity,
    int Score,
    string Action,
    IReadOnlyList<string> Rules,
    string Summary,
    Guid? CaseId,
    DateTimeOffset RaisedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>A user's wallets were frozen or unfrozen, by a rule, a ring or a compliance officer.</summary>
[IntegrationEvent("fraud.account_freeze_changed", KafkaTopics.FraudAlerts)]
public sealed record AccountFreezeChanged(Guid UserId, bool Frozen, string Reason, string By, DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}

/// <summary>A case was opened, assigned or closed (M10.7).</summary>
[IntegrationEvent("fraud.case_changed", KafkaTopics.FraudAlerts)]
public sealed record FraudCaseChanged(Guid CaseId, Guid UserId, string Kind, string Status, string? Resolution, string? AssignedTo, DateTimeOffset ChangedAt) : IntegrationEvent
{
    public override string GetPartitionKey() => UserId.ToString();
}
