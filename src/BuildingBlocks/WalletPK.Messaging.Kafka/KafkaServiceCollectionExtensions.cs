using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalletPK.Messaging.Abstractions;

namespace WalletPK.Messaging.Kafka;

public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Kafka settings (section "Kafka"), the producer used by the outbox relay, and optional topic
    /// creation at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="clientId">Default client ID (the service name) when the configuration sets none.</param>
    public static IServiceCollection AddWalletPkKafka(this IServiceCollection services, string clientId)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<KafkaOptions>()
            .BindConfiguration(KafkaOptions.SectionName)
            .PostConfigure(options => options.ClientId ??= clientId)
            .Validate(options => options.IsValid(), "Kafka: BootstrapServers is required and MessageTimeoutMilliseconds must be >= 1000.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<KafkaOptions>>().Value);

        services.TryAddSingleton<KafkaEventPublisher>();
        services.TryAddSingleton<IEventPublisher>(provider => provider.GetRequiredService<KafkaEventPublisher>());

        // Registered first, so topics exist before consumers subscribe.
        services.TryAddSingleton<KafkaTopicAdmin>();
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(provider => provider.GetRequiredService<KafkaTopicAdmin>()));
        return services;
    }

    /// <summary>
    /// Adds a consumer group that reads <paramref name="topics"/> and passes each message to the
    /// scoped <see cref="IEnvelopeProcessor"/> (normally the inbox processor).
    /// </summary>
    public static IServiceCollection AddKafkaConsumer(this IServiceCollection services, string groupId, params string[] topics)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (topics is not { Length: > 0 } || !topics.All(KafkaTopics.All.Contains))
        {
            throw new ArgumentException("Subscribe to at least one topic, each listed in KafkaTopics.", nameof(topics));
        }

        var definition = new KafkaConsumerDefinition(groupId, topics);
        services.AddSingleton<IHostedService>(provider => new KafkaConsumerService(
            definition,
            provider.GetRequiredService<KafkaOptions>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<ILogger<KafkaConsumerService>>()));
        return services;
    }
}
