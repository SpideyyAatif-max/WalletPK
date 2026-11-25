using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WalletPK.Contracts.Fraud;
using WalletPK.Contracts.Identity;
using WalletPK.Contracts.Ledger;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Infrastructure.Persistence;
using WalletPK.Fraud.Infrastructure.Redis;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Web.Health;

namespace WalletPK.Fraud.Infrastructure;

public static class FraudInfrastructureExtensions
{
    /// <summary>The Kafka consumer group (and inbox consumer name).</summary>
    public const string ConsumerGroup = "fraud";

    public const string DatabaseConnectionName = "FraudDb";

    public const string RedisConnectionName = "Redis";

    /// <summary>Registers the fraud use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddFraudServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        services.AddOptions<FraudOptions>()
            .BindConfiguration(FraudOptions.SectionName)
            .Validate(options => options.IsValid(), "Fraud: thresholds 1-100 with StepUpAt <= ReviewAt <= BlockAt, ring settings positive with RingMinFlagged <= RingMinSenders.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FraudOptions>>().Value);

        // ---- Events and commands: identity and ledger events in, freeze commands and fraud alerts out ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(UserRegistered).Assembly, typeof(UserRegistered).Namespace)
            .RegisterFromAssembly(typeof(FreezeWallets).Assembly, typeof(FreezeWallets).Namespace)
            .RegisterFromAssembly(typeof(FraudAlertRaised).Assembly, typeof(FraudAlertRaised).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        services.AddWalletPkKafka(clientId: "fraud");
        services.AddOutboxRelay<FraudDbContext>();
        services.AddInbox<FraudDbContext>();
        services.AddKafkaConsumer(ConsumerGroup, KafkaTopics.IdentityEvents, KafkaTopics.LedgerEvents, KafkaTopics.LedgerReplies);
        services.AddIntegrationEventHandler<UserRegistered, FraudEventHandlers>();
        services.AddIntegrationEventHandler<DeviceBound, FraudEventHandlers>();
        services.AddIntegrationEventHandler<PaymentStepUpConfirmed, FraudEventHandlers>();
        services.AddIntegrationEventHandler<WalletOpened, FraudEventHandlers>();
        services.AddIntegrationEventHandler<WalletEntryPosted, FraudEventHandlers>();
        services.AddIntegrationEventHandler<WalletsFreezeChanged, FraudEventHandlers>();

        // ---- Redis: feature windows, score cache, the rules change channel ----
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            ConnectionMultiplexer.Connect(RequiredConnectionString(provider, RedisConnectionName)));
        services.AddSingleton<IFeatureStore, RedisFeatureStore>();
        services.AddSingleton<IRuleCache, RuleCache>();
        services.AddHostedService<RuleReloader>();
        services.AddHostedService<FraudWarmup>();

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<FraudDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IFraudStore, FraudStore>();

        // ---- Use cases; ring detection in the background (M10.5) ----
        services.AddSingleton<RingQueue>();
        services.AddHostedService<RingDetectionService>();
        services.AddScoped<RiskScorer>();
        services.AddScoped<AlertService>();
        services.AddScoped<AccountService>();
        services.AddScoped<RingDetector>();
        services.AddScoped<CaseService>();
        services.AddScoped<RuleService>();
        services.AddScoped<AuditService>();
        services.AddScoped<SuspiciousActivityService>();

        services.AddHealthChecks()
            .AddDbContextCheck<FraudDbContext>("postgres", tags: [HealthCheckTags.Ready])
            .AddCheck<RedisHealthCheck>("redis", tags: [HealthCheckTags.Ready]);

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
