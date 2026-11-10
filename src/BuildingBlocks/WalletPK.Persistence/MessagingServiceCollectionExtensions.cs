using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Dispatching;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Persistence;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Publishes <typeparamref name="TDbContext"/>'s outbox to Kafka in the background (M4.1).
    /// Needs an <see cref="IEventPublisher"/> (AddWalletPkKafka) and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddOutboxRelay<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OutboxRelayOptions>()
            .BindConfiguration(OutboxRelayOptions.SectionName)
            .Validate(options => options.IsValid(), "Outbox:Relay: BatchSize 1-5000, PollIntervalMilliseconds >= 10, RetentionHours >= 1, MaxBackoffSeconds >= 1.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<OutboxRelayOptions>>().Value);
        services.AddScoped<OutboxRelay<TDbContext>>();
        services.AddHostedService<OutboxRelayService<TDbContext>>();
        return services;
    }

    /// <summary>
    /// Makes consumed events go through <typeparamref name="TDbContext"/>'s inbox (M4.3): registers the
    /// <see cref="IEnvelopeProcessor"/> the Kafka consumer host calls, the handler dispatcher and the
    /// hourly inbox cleanup. Register handlers with AddIntegrationEventHandler.
    /// </summary>
    public static IServiceCollection AddInbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<InboxOptions>()
            .BindConfiguration(InboxOptions.SectionName)
            .Validate(options => options.RetentionDays >= 31, "Inbox:RetentionDays must be longer than the longest Kafka retention (30 days).")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<InboxOptions>>().Value);
        services.TryAddScoped<IntegrationEventDispatcher>();
        services.AddScoped<IEnvelopeProcessor, InboxEnvelopeProcessor<TDbContext>>();
        services.AddHostedService<InboxCleanupService<TDbContext>>();
        return services;
    }
}
