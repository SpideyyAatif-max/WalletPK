using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Messaging.Kafka;

/// <summary>
/// Creates WalletPK topics that do not exist yet, with the partition counts and retention of
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

    /// <summary>
    /// How long each topic keeps messages (retention.ms; -1 = forever). ledger.events is kept forever
    /// because read models are rebuilt by replaying it from the start (M4.6).
    /// </summary>
    public static IReadOnlyDictionary<string, long> RetentionMilliseconds { get; } = new Dictionary<string, long>
    {
        [KafkaTopics.IdentityEvents] = SevenDays,
        [KafkaTopics.LedgerEvents] = -1,
        [KafkaTopics.PaymentsEvents] = SevenDays,
        [KafkaTopics.BillsEvents] = SevenDays,
        [KafkaTopics.TopupEvents] = SevenDays,
        [KafkaTopics.AgentsEvents] = SevenDays,
        [KafkaTopics.FraudAlerts] = ThirtyDays,
        [KafkaTopics.NotificationsEvents] = SevenDays,
        [KafkaTopics.PaymentsDeadLetter] = ThirtyDays,
        [KafkaTopics.BillsDeadLetter] = ThirtyDays,
        [KafkaTopics.TopupDeadLetter] = ThirtyDays,
        [KafkaTopics.SimulationMetrics] = OneDay,
    };

    private const long OneDay = 86_400_000;
    private const long SevenDays = 7 * OneDay;
    private const long ThirtyDays = 30 * OneDay;

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
                Configs = new Dictionary<string, string>
                {
                    ["retention.ms"] = RetentionMilliseconds.GetValueOrDefault(topic, SevenDays).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
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

    /// <summary>
    /// Deletes a consumer group and its committed offsets, for example the previous generation of a
    /// rebuilt read model. Returns false (and logs) when Kafka refuses, for instance because a member is
    /// still connected; a leftover group is harmless and expires with the broker's offset retention.
    /// </summary>
    public async Task<bool> DeleteConsumerGroupAsync(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
        try
        {
            await admin.DeleteGroupsAsync([groupId], new DeleteGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
            return true;
        }
        catch (DeleteGroupsException exception)
        {
            LogGroupNotDeleted(logger, groupId, string.Join(", ", exception.Results.Select(result => result.Error.Reason)));
            return false;
        }
        catch (KafkaException exception)
        {
            LogGroupNotDeleted(logger, groupId, exception.Error.Reason);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka consumer group {GroupId} was not deleted: {Reason}")]
    private static partial void LogGroupNotDeleted(ILogger logger, string groupId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Kafka topics: {Topics}")]
    private static partial void LogCreated(ILogger logger, string topics);
}
