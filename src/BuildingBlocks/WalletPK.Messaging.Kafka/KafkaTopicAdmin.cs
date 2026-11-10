using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Messaging.Kafka;

/// <summary>
/// Creates WalletPK topics that do not exist yet, with the partition counts of
/// ops/kafka/create-topics.sh. Runs at startup when <see cref="KafkaOptions.EnsureTopics"/> is on.
/// </summary>
public sealed partial class KafkaTopicAdmin(KafkaOptions options, ILogger<KafkaTopicAdmin> logger) : IHostedService
{
    /// <summary>Partitions per topic: more for busier topics. Keys (wallet / user IDs) spread over them.</summary>
    public static IReadOnlyDictionary<string, int> Partitions { get; } = new Dictionary<string, int>
    {
        [KafkaTopics.IdentityEvents] = 6,
        [KafkaTopics.LedgerEvents] = 12,
        [KafkaTopics.PaymentsEvents] = 6,
        [KafkaTopics.BillsEvents] = 6,
        [KafkaTopics.TopupEvents] = 6,
        [KafkaTopics.AgentsEvents] = 3,
        [KafkaTopics.FraudAlerts] = 3,
        [KafkaTopics.NotificationsEvents] = 6,
        [KafkaTopics.PaymentsDeadLetter] = 3,
        [KafkaTopics.BillsDeadLetter] = 3,
        [KafkaTopics.TopupDeadLetter] = 3,
        [KafkaTopics.SimulationMetrics] = 3,
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.EnsureTopics)
        {
            await EnsureTopicsAsync(KafkaTopics.All, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnsureTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

        var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics
            .Where(topic => topic.Error.Code == ErrorCode.NoError)
            .Select(topic => topic.Topic)
            .ToHashSet(StringComparer.Ordinal);

        var missing = topics
            .Where(topic => !existing.Contains(topic))
            .Select(topic => new TopicSpecification
            {
                Name = topic,
                NumPartitions = Partitions.GetValueOrDefault(topic, 3),
                ReplicationFactor = 1,
            })
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missing);
        }
        catch (CreateTopicsException exception) when (exception.Results.TrueForAll(result =>
            result.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Another instance created them at the same moment.
        }

        cancellationToken.ThrowIfCancellationRequested();
        LogCreated(logger, string.Join(", ", missing.Select(topic => topic.Name)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Kafka topics: {Topics}")]
    private static partial void LogCreated(ILogger logger, string topics);
}
