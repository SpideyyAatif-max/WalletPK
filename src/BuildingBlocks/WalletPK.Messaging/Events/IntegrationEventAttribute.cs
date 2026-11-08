namespace WalletPK.Messaging.Events;

/// <summary>
/// Gives an integration event a stable public name and its Kafka topic, independent of the C# class
/// name, so classes can be renamed or moved without breaking consumers.
/// <code>
/// [IntegrationEvent("ledger.money_transferred", KafkaTopics.LedgerEvents)]
/// public sealed record MoneyTransferred(...) : IntegrationEvent;
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class IntegrationEventAttribute(string name, string topic) : Attribute
{
    /// <summary>Stable name: lowercase "&lt;area&gt;.&lt;fact_in_past_tense&gt;", for example "ledger.money_transferred".</summary>
    public string Name { get; } = name;

    /// <summary>The Kafka topic, one of <see cref="KafkaTopics"/>.</summary>
    public string Topic { get; } = topic;

    /// <summary>Schema version. Increase it only for a breaking change; add fields without bumping it.</summary>
    public int Version { get; set; } = 1;
}
