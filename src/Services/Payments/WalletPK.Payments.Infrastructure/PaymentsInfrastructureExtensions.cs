using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Payments.Application.BankTransfers;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Infrastructure.Persistence;
using WalletPK.Payments.Infrastructure.Raast;
using WalletPK.Persistence;
using WalletPK.Sagas;
using WalletPK.Web.Health;

namespace WalletPK.Payments.Infrastructure;

public static class PaymentsInfrastructureExtensions
{
    /// <summary>The Payments Kafka consumer group (and inbox consumer name).</summary>
    public const string PaymentsConsumerGroup = "payments";

    public const string DatabaseConnectionName = "PaymentsDb";

    /// <summary>Registers the Payments use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddPaymentsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        // ---- Options (validated at startup) ----
        services.AddOptions<BankTransferOptions>()
            .BindConfiguration(BankTransferOptions.SectionName)
            .Validate(options => options.IsValid(), "Payments:BankTransfers: positive timings, HoldLifetimeHours (at most 168) longer than MaxQueueHours + MaxWaitHours, and a SenderName.")
            .ValidateOnStart();
        services.AddOptions<RaastOptions>()
            .BindConfiguration(RaastOptions.SectionName)
            .Validate(options => options.IsValid(), "Providers:Raast: BaseUrl (absolute), ApiKey, and valid Resilience settings.")
            .ValidateOnStart();
        services.AddOptions<RaastWebhookOptions>()
            .BindConfiguration(RaastWebhookOptions.SectionName)
            .Validate(options => options.ToleranceSeconds >= 30, "Webhooks:Raast:ToleranceSeconds must be at least 30.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<BankTransferOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<RaastOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<RaastWebhookOptions>>().Value);

        // ---- Events and commands: Ledger hold commands and replies, payments.* events ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(PlaceHold).Assembly, typeof(PlaceHold).Namespace)
            .RegisterFromAssembly(typeof(WalletPK.Contracts.Payments.BankTransferChanged).Assembly, typeof(WalletPK.Contracts.Payments.BankTransferChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        // ---- Kafka (M4): the outbox out (ledger.commands, payments.events), the inbox in (ledger.replies) ----
        services.AddWalletPkKafka(clientId: "payments");
        services.AddOutboxRelay<PaymentsDbContext>();
        services.AddInbox<PaymentsDbContext>();
        services.AddKafkaConsumer(PaymentsConsumerGroup, KafkaTopics.LedgerReplies);

        // ---- Sagas (M6.3): the Raast transfer, driven by Ledger replies, Raast callbacks and timers ----
        services.AddSagas<PaymentsDbContext>();
        services.AddSaga<BankTransferSaga>();
        services.AddSagaMessage<HoldPlaced, PaymentsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldRejected, PaymentsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptured, PaymentsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptureFailed, PaymentsDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldReleased, PaymentsDbContext>(reply => reply.HoldId);

        // ---- Raast (M6.5): one resilience pipeline per bank ----
        services.AddSingleton<RaastMetrics>();
        services.AddSingleton<RaastResilience>();
        services.AddSingleton<IBankAvailability>(provider => provider.GetRequiredService<RaastResilience>());
        services.AddHttpClient<IRaastGateway, RaastGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<RaastOptions>();
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);

            // Time limits are the pipeline's (attempt and total timeouts), not HttpClient's.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<PaymentsDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IBankTransferQueries, BankTransferQueries>();

        // ---- Use cases ----
        services.AddScoped<BankTransferService>();

        // ---- Readiness: ready only when the database is reachable ----
        services.AddHealthChecks()
            .AddDbContextCheck<PaymentsDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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

/// <summary>Raast's callbacks, from "Webhooks:Raast". The gateway checks the signature first; Payments checks it again.</summary>
public sealed class RaastWebhookOptions
{
    public const string SectionName = "Webhooks:Raast";

    /// <summary>The shared secret Raast signs callbacks with (empty: callbacks are refused).</summary>
    public string Secret { get; set; } = "";

    /// <summary>How old a callback's timestamp may be.</summary>
    public int ToleranceSeconds { get; set; } = 300;
}
