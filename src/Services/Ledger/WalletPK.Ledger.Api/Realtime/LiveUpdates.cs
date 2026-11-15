using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using WalletPK.Messaging;
using WalletPK.Messaging.Kafka;

namespace WalletPK.Ledger.Api.Realtime;

/// <summary>Live-update settings, from "Ledger:LiveUpdates".</summary>
public sealed class LiveUpdatesOptions
{
    public const string SectionName = "Ledger:LiveUpdates";

    /// <summary>Consume ledger.events and push new entries to browsers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Each instance uses its own consumer group "{prefix}-{random}", deleted when it stops.</summary>
    public string ConsumerGroupPrefix { get; set; } = "ledger-live";
}

/// <summary>
/// Feeds <see cref="WalletHub"/> from Kafka. Every instance of the service reads every ledger event
/// (its own consumer group, starting at the newest message), because a user's browser can be connected
/// to any instance; each instance pushes only to the connections it holds. That avoids a backplane
/// for now (M11.1 adds one with the Notifications service).
/// </summary>
internal sealed partial class LiveUpdatesHost(
    LiveUpdatesOptions options,
    KafkaOptions kafkaOptions,
    KafkaTopicAdmin topicAdmin,
    IServiceScopeFactory scopes,
    IMeterFactory meterFactory,
    ILoggerFactory loggerFactory,
    ILogger<LiveUpdatesHost> logger) : IHostedService, IDisposable
{
    private readonly string _groupId = $"{options.ConsumerGroupPrefix}-{Guid.NewGuid():N}";
    private KafkaConsumerService? _consumer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        _consumer = new KafkaConsumerService(
            new KafkaConsumerDefinition(_groupId, [KafkaTopics.LedgerEvents], typeof(LiveWalletNotifier), StartFromLatest: true),
            kafkaOptions,
            scopes,
            meterFactory,
            loggerFactory.CreateLogger<KafkaConsumerService>());
        await _consumer.StartAsync(cancellationToken);
        LogStarted(logger, _groupId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumer is null)
        {
            return;
        }

        await _consumer.StopAsync(cancellationToken);
        await topicAdmin.DeleteConsumerGroupAsync(_groupId);
    }

    public void Dispose() => _consumer?.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushing live wallet updates (consumer group {GroupId})")]
    private static partial void LogStarted(ILogger logger, string groupId);
}

public static class LiveUpdatesExtensions
{
    /// <summary>SignalR, the wallet hub and the Kafka consumer that feeds it.</summary>
    public static IServiceCollection AddLiveWalletUpdates(this IServiceCollection services)
    {
        services.AddSignalR()
            .AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();

        services.AddOptions<LiveUpdatesOptions>()
            .BindConfiguration(LiveUpdatesOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConsumerGroupPrefix), "Ledger:LiveUpdates:ConsumerGroupPrefix is required.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<LiveUpdatesOptions>>().Value);
        services.AddScoped<LiveWalletNotifier>();
        services.AddHostedService<LiveUpdatesHost>();
        return services;
    }

    public static IEndpointRouteBuilder MapLiveWalletUpdates(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<WalletHub>(WalletHub.Path).RequireAuthorization();
        return endpoints;
    }
}
