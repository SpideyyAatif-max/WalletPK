namespace WalletPK.Messaging.Kafka;

/// <summary>Kafka connection settings, from the "Kafka" configuration section.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Broker addresses: "localhost:9094" from your machine (the compose EXTERNAL listener),
    /// "kafka:9092" from containers on the compose network.
    /// </summary>
    public string BootstrapServers { get; set; } = "localhost:9094";

    /// <summary>Shown in broker logs and metrics; defaults to the service name.</summary>
    public string? ClientId { get; set; }

    /// <summary>How long the producer keeps trying to deliver one message before reporting failure.</summary>
    public int MessageTimeoutMilliseconds { get; set; } = 15_000;

    /// <summary>
    /// Create the WalletPK topics at startup if they are missing (handy for local runs and tests;
    /// in compose the kafka-init container creates them, and production would manage them separately).
    /// </summary>
    public bool EnsureTopics { get; set; }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(BootstrapServers) && MessageTimeoutMilliseconds >= 1_000;
}
