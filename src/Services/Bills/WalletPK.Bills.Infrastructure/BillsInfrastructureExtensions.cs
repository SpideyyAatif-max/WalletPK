using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Application.Payments;
using WalletPK.Bills.Application.Saved;
using WalletPK.Bills.Infrastructure.Billers;
using WalletPK.Bills.Infrastructure.Persistence;
using WalletPK.Bills.Infrastructure.Reminders;
using WalletPK.Contracts.Bills;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Sagas;
using WalletPK.Web.Health;

namespace WalletPK.Bills.Infrastructure;

public static class BillsInfrastructureExtensions
{
    /// <summary>The Bills Kafka consumer group (and inbox consumer name).</summary>
    public const string BillsConsumerGroup = "bills";

    public const string DatabaseConnectionName = "BillsDb";

    /// <summary>Registers the Bills use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddBillsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        // ---- Options (validated at startup) ----
        services.AddOptions<BillPaymentOptions>()
            .BindConfiguration(BillPaymentOptions.SectionName)
            .Validate(options => options.IsValid(), "Bills:Payments: positive timings, and HoldLifetimeHours (at most 168) longer than every automatic step.")
            .ValidateOnStart();
        services.AddOptions<BillerOptions>()
            .BindConfiguration(BillerOptions.SectionName)
            .Validate(options => options.IsValid(), "Providers:Billers: BaseUrl (absolute), ApiKey, and valid Resilience settings.")
            .ValidateOnStart();
        services.AddOptions<BillReminderOptions>()
            .BindConfiguration(BillReminderOptions.SectionName)
            .Validate(options => options.IsValid(), "Bills:Reminders: positive PollSeconds, BatchSize 1-1000, CheckIntervalHours, RetryMinutes; DaysBefore 0-20.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BillPaymentOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BillerOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BillReminderOptions>>().Value);

        // ---- Events and commands: Ledger holds, bills.* events ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(PlaceHold).Assembly, typeof(PlaceHold).Namespace)
            .RegisterFromAssembly(typeof(BillPaymentChanged).Assembly, typeof(BillPaymentChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        // ---- Kafka: the outbox out (ledger.commands, bills.events, bills.dlq), the inbox in (ledger.replies) ----
        services.AddWalletPkKafka(clientId: "bills");
        services.AddOutboxRelay<BillsDbContext>();
        services.AddInbox<BillsDbContext>();
        services.AddKafkaConsumer(BillsConsumerGroup, KafkaTopics.LedgerReplies);

        // ---- Sagas: the bill payment, driven by Ledger replies, timers and operations ----
        services.AddSagas<BillsDbContext>();
        services.AddSaga<BillPaymentSaga>();
        services.AddSagaMessage<HoldPlaced, BillsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldRejected, BillsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptured, BillsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptureFailed, BillsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldReleased, BillsDbContext>(reply => reply.HoldId);

        // ---- Billers: one resilience pipeline per biller ----
        services.AddSingleton<BillerMetrics>();
        services.AddSingleton<BillerResilience>();
        services.AddSingleton<IBillerAvailability>(provider => provider.GetRequiredService<BillerResilience>());
        services.AddHttpClient<IBillerGateway, BillerGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<BillerOptions>();
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<BillsDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IBillPaymentQueries, BillPaymentQueries>();
        services.AddScoped<ISavedBillers, SavedBillers>();

        // ---- Use cases and the reminder job (M7.6) ----
        services.AddScoped<BillsService>();
        services.AddScoped<SavedBillersService>();
        services.AddSingleton<BillReminderService>();
        services.AddHostedService(provider => provider.GetRequiredService<BillReminderService>());

        services.AddHealthChecks()
            .AddDbContextCheck<BillsDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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
