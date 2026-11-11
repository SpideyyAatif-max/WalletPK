using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging;
using WalletPK.Messaging.Kafka;

namespace WalletPK.Ledger.Infrastructure.ReadModels;

/// <summary>
/// Runs the read-model projection: a Kafka consumer on ledger.events whose group is named after the
/// current generation ("ledger-read-models-g1"). Every instance checks the generation every couple of
/// seconds; when a rebuild bumps it (M4.6), the instance stops the old consumer and starts one in the
/// new group, which has no committed offsets and so reads the topic from offset 0. Instances of the
/// service share each group, so Kafka spreads the partitions over them.
/// </summary>
internal sealed partial class ReadModelProjectionHost(
    ReadModelOptions options,
    KafkaOptions kafkaOptions,
    KafkaTopicAdmin topicAdmin,
    LedgerReadModelDatabase database,
    IServiceScopeFactory scopes,
    IMeterFactory meterFactory,
    ILoggerFactory loggerFactory,
    ILogger<ReadModelProjectionHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ProjectionEnabled)
        {
            return;
        }

        KafkaConsumerService? consumer = null;
        long running = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var generation = await CurrentGenerationAsync(stoppingToken);
                    if (generation != running)
                    {
                        if (consumer is not null)
                        {
                            await consumer.StopAsync(CancellationToken.None);
                            consumer.Dispose();
                            consumer = null;
                            await topicAdmin.DeleteConsumerGroupAsync(options.ConsumerGroupFor(running));
                        }

                        consumer = new KafkaConsumerService(
                            new KafkaConsumerDefinition(options.ConsumerGroupFor(generation), [KafkaTopics.LedgerEvents], typeof(LedgerProjector)),
                            kafkaOptions,
                            scopes,
                            meterFactory,
                            loggerFactory.CreateLogger<KafkaConsumerService>());
                        await consumer.StartAsync(stoppingToken);
                        LogProjecting(logger, generation, options.ConsumerGroupFor(generation));
                        running = generation;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // For example the database is briefly down; the running consumer (if any) carries on.
                    LogCheckFailed(logger, exception);
                }

                await Task.Delay(options.GenerationPollMilliseconds, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            if (consumer is not null)
            {
                await consumer.StopAsync(CancellationToken.None);
                consumer.Dispose();
            }
        }
    }

    private async Task<long> CurrentGenerationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT generation FROM read.projection_state WHERE name = @Name",
            new { Name = LedgerReadModelDatabase.ProjectionName },
            cancellationToken: cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Projecting read models, generation {Generation} (consumer group {GroupId})")]
    private static partial void LogProjecting(ILogger logger, long generation, string groupId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the read-model generation")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
