using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.UnitTests.InvalidEvents;

public sealed record WithoutMetadata(string Value) : IntegrationEvent;

[IntegrationEvent("Ledger Money Moved", KafkaTopics.LedgerEvents)]
public sealed record BadName(string Value) : IntegrationEvent;

[IntegrationEvent("ledger.something_happened", "not.a.topic")]
public sealed record UnknownTopic(string Value) : IntegrationEvent;

[IntegrationEvent("ledger.money_transferred", KafkaTopics.LedgerEvents)]
public sealed record DuplicateName(string Value) : IntegrationEvent;
