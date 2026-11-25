using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Payments;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Reconciliation.Application;
using WalletPK.Reconciliation.Infrastructure.Persistence;
using WalletPK.Web.Health;

namespace WalletPK.Reconciliation.Infrastructure;

public static class ReconciliationInfrastructureExtensions
{
    /// <summary>The Kafka consumer group (and inbox consumer name).</summary>
    public const string ConsumerGroup = "reconciliation";

    public const string DatabaseConnectionName = "ReconciliationDb";

    /// <summary>The topics carrying payments that leave WalletPK.</summary>
    public static IReadOnlyList<string> Topics { get; } = [KafkaTopics.PaymentsEvents, KafkaTopics.BillsEvents, KafkaTopics.TopupEvents];

    /// <summary>Registers the reconciliation use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddReconciliationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        services.AddOptions<ReconciliationOptions>()
            .BindConfiguration(ReconciliationOptions.SectionName)
            .Validate(options => options.IsValid(), "Reconciliation: Parties are bank:, biller: or telco: codes; Schedule:DailyAt is HH:mm, PollSeconds 1-3600, RetryMinutes 1-1440.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value);

        // ---- Events in: the payment services' changes to bank transfers, bill payments and top-ups ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(BankTransferChanged).Assembly, typeof(BankTransferChanged).Namespace)
            .RegisterFromAssembly(typeof(BillPaymentChanged).Assembly, typeof(BillPaymentChanged).Namespace)
            .RegisterFromAssembly(typeof(TopUpChanged).Assembly, typeof(TopUpChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        services.AddWalletPkKafka(clientId: "reconciliation");
        services.AddInbox<ReconciliationDbContext>();
        services.AddSingleton<IHostedService>(provider => new KafkaConsumerService(
            new KafkaConsumerDefinition(ConsumerGroup, Topics, StartFromLatest: provider.GetRequiredService<ReconciliationOptions>().StartFromLatest),
            provider.GetRequiredService<KafkaOptions>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<ILogger<KafkaConsumerService>>()));

        services.AddIntegrationEventHandler<BankTransferChanged, PaymentEventHandlers>();
        services.AddIntegrationEventHandler<BillPaymentChanged, PaymentEventHandlers>();
        services.AddIntegrationEventHandler<TopUpChanged, PaymentEventHandlers>();

        // ---- Settlement files ----
        services.AddOptions<SettlementSourceOptions>()
            .BindConfiguration(SettlementSourceOptions.SectionName)
            .Validate(options => options.IsValid(), "Providers:Settlement: BaseUrl (absolute), ApiKey and TimeoutSeconds (1-300) are required.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<SettlementSourceOptions>>().Value);
        services.AddHttpClient<ISettlementSource, SettlementClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<SettlementSourceOptions>();
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<ReconciliationDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IReconStore, ReconStore>();

        // ---- Use cases; the nightly run in the background ----
        services.AddSingleton<IReconMetrics, ReconMetrics>();
        services.AddScoped<PaymentTracker>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<BreakService>();
        services.AddScoped<AuditService>();
        services.AddHostedService<ReconciliationScheduler>();

        services.AddHealthChecks()
            .AddDbContextCheck<ReconciliationDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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
