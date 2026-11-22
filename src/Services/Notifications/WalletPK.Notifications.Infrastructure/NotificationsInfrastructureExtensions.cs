using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Fraud;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.Notifications;
using WalletPK.Contracts.Payments;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Notifications.Application;
using WalletPK.Notifications.Infrastructure.Persistence;
using WalletPK.Persistence;
using WalletPK.Persistence.Inbox;
using WalletPK.Web.Health;

namespace WalletPK.Notifications.Infrastructure;

public static class NotificationsInfrastructureExtensions
{
    /// <summary>The Kafka consumer group (and inbox consumer name).</summary>
    public const string ConsumerGroup = "notifications";

    public const string DatabaseConnectionName = "NotificationsDb";

    /// <summary>Every topic with something to tell users or staff.</summary>
    public static IReadOnlyList<string> Topics { get; } =
    [
        KafkaTopics.IdentityEvents, KafkaTopics.LedgerEvents, KafkaTopics.PaymentsEvents, KafkaTopics.BillsEvents, KafkaTopics.TopupEvents,
        KafkaTopics.AgentsEvents, KafkaTopics.FraudAlerts, KafkaTopics.BillsDeadLetter, KafkaTopics.TopupDeadLetter,
    ];

    /// <summary>
    /// Registers the notification use cases and their adapters. The host provides <see cref="IRealtime"/>
    /// (the SignalR hubs). Settings are read lazily from configuration.
    /// </summary>
    public static IServiceCollection AddNotificationsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        services.AddOptions<NotificationsOptions>()
            .BindConfiguration(NotificationsOptions.SectionName)
            .Validate(options => options.IsValid(), "Notifications: SendWindowMinutes > 0, MaxAttempts 1-50, RetryInitialSeconds > 0 and <= RetryMaxSeconds, SenderPollMilliseconds 20-60000.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<NotificationsOptions>>().Value);

        // ---- Events in (every service's), delivery reports out ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(UserRegistered).Assembly, typeof(UserRegistered).Namespace)
            .RegisterFromAssembly(typeof(WalletEntryPosted).Assembly, typeof(WalletEntryPosted).Namespace)
            .RegisterFromAssembly(typeof(P2PTransferChanged).Assembly, typeof(P2PTransferChanged).Namespace)
            .RegisterFromAssembly(typeof(BillPaymentChanged).Assembly, typeof(BillPaymentChanged).Namespace)
            .RegisterFromAssembly(typeof(TopUpChanged).Assembly, typeof(TopUpChanged).Namespace)
            .RegisterFromAssembly(typeof(AgentStatusChanged).Assembly, typeof(AgentStatusChanged).Namespace)
            .RegisterFromAssembly(typeof(FraudAlertRaised).Assembly, typeof(FraudAlertRaised).Namespace)
            .RegisterFromAssembly(typeof(NotificationDeliveryChanged).Assembly, typeof(NotificationDeliveryChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        services.AddWalletPkKafka(clientId: "notifications");
        services.AddOutboxRelay<NotificationsDbContext>();
        services.AddInbox<NotificationsDbContext>();
        services.AddScoped<InboxEnvelopeProcessor<NotificationsDbContext>>();
        services.AddScoped<NotificationsEnvelopeProcessor>();
        services.AddSingleton<IHostedService>(provider => new KafkaConsumerService(
            new KafkaConsumerDefinition(ConsumerGroup, Topics, typeof(NotificationsEnvelopeProcessor), provider.GetRequiredService<NotificationsOptions>().StartFromLatest),
            provider.GetRequiredService<KafkaOptions>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<ILogger<KafkaConsumerService>>()));

        services.AddIntegrationEventHandler<UserRegistered, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<DeviceBound, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<KycUpgraded, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<WalletOpened, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<WalletEntryPosted, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<P2PTransferChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<BankTransferChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<MoneyRequestChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<BillPaymentChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<BillDueReminder, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<BillPaymentDeadLettered, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<TopUpChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<TopUpDeadLettered, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<MerchantPaymentChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<AgentStatusChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<AgentTransactionChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<AgentFloatLow, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<FraudAlertRaised, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<AccountFreezeChanged, NotificationEventHandlers>();
        services.AddIntegrationEventHandler<FraudCaseChanged, NotificationEventHandlers>();

        // ---- SMS and email gateways ----
        services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.SectionName)
            .Validate(options => options.IsValid(), "Providers:Messaging: BaseUrl (absolute), ApiKey and valid Resilience settings are required.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MessagingOptions>>().Value);
        services.AddSingleton<MessagingPipelines>();
        services.AddHttpClient<IMessageGateway, MessageGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<MessagingOptions>();
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<NotificationsDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<INotificationStore, NotificationStore>();

        // ---- Use cases; the sender in the background ----
        services.AddScoped<RealtimeOutbox>();
        services.AddSingleton<DeliverySignal>();
        services.AddScoped<Notifier>();
        services.AddScoped<Recipients>();
        services.AddScoped<NotificationCentre>();
        services.AddScoped<DeliveryService>();
        services.AddHostedService<DeliverySender>();

        services.AddHealthChecks()
            .AddDbContextCheck<NotificationsDbContext>("postgres", tags: [HealthCheckTags.Ready]);

        return services;
    }

    private static string RequiredConnectionString(IServiceProvider provider, string name)
    {
        var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"Connection string '{name}' is not configured.")
            : connectionString;
    }
}
