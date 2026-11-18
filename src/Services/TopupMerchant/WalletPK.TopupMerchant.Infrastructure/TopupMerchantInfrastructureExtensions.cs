using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Contracts.Ledger;
using WalletPK.Contracts.TopupMerchant;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.TopupMerchant.Infrastructure.Merchants;
using WalletPK.TopupMerchant.Infrastructure.Persistence;
using WalletPK.TopupMerchant.Infrastructure.Settlement;
using WalletPK.TopupMerchant.Infrastructure.Telcos;
using WalletPK.Web.Health;

namespace WalletPK.TopupMerchant.Infrastructure;

public static class TopupMerchantInfrastructureExtensions
{
    /// <summary>The Kafka consumer group (and inbox consumer name).</summary>
    public const string ConsumerGroup = "topup-merchant";

    public const string DatabaseConnectionName = "TopupMerchantDb";

    /// <summary>Registers the top-up and merchant use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddTopupMerchantServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        // ---- Options (validated at startup) ----
        services.AddOptions<TopUpOptions>()
            .BindConfiguration(TopUpOptions.SectionName)
            .Validate(options => options.IsValid(), "TopUps: positive timings, and HoldLifetimeHours (at most 168) longer than every automatic step.")
            .ValidateOnStart();
        services.AddOptions<TelcoOptions>()
            .BindConfiguration(TelcoOptions.SectionName)
            .Validate(options => options.IsValid(), "Providers:Telcos: BaseUrl (absolute), ApiKey, and valid Resilience settings.")
            .ValidateOnStart();
        services.AddOptions<MerchantPaymentOptions>()
            .BindConfiguration(MerchantPaymentOptions.SectionName)
            .Validate(options => options.IsValid(), "Merchants:Payments: positive LedgerReplyTimeoutSeconds and MaxLedgerResends.")
            .ValidateOnStart();
        services.AddOptions<QrOptions>()
            .BindConfiguration(QrOptions.SectionName)
            .Validate(options => options.IsValid(), "Merchants:Qr:SigningKey: at least 32 characters.")
            .ValidateOnStart();
        services.AddOptions<MerchantSettlementOptions>()
            .BindConfiguration(MerchantSettlementOptions.SectionName)
            .Validate(options => options.IsValid(), "Merchants:Settlement: positive PollSeconds and ResendMinutes.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TopUpOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TelcoOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MerchantPaymentOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<QrOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MerchantSettlementOptions>>().Value);

        // ---- Events and commands: Ledger holds and merchant commands, topup.* events ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(PlaceHold).Assembly, typeof(PlaceHold).Namespace)
            .RegisterFromAssembly(typeof(TopUpChanged).Assembly, typeof(TopUpChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        // ---- Kafka: the outbox out (ledger.commands, topup.events, topup.dlq), the inbox in (ledger.replies) ----
        services.AddWalletPkKafka(clientId: "topup-merchant");
        services.AddOutboxRelay<TopupMerchantDbContext>();
        services.AddInbox<TopupMerchantDbContext>();
        services.AddKafkaConsumer(ConsumerGroup, KafkaTopics.LedgerReplies);

        // ---- Sagas: top-ups (Ledger holds, timers) and QR payments (Ledger replies, timers) ----
        services.AddSagas<TopupMerchantDbContext>();
        services.AddSaga<TopUpSaga>();
        services.AddSaga<MerchantPaymentSaga>();
        services.AddSagaMessage<HoldPlaced, TopupMerchantDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldRejected, TopupMerchantDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptured, TopupMerchantDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldCaptureFailed, TopupMerchantDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<HoldReleased, TopupMerchantDbContext>(reply => reply.HoldId);
        services.AddSagaMessage<MerchantPaid, TopupMerchantDbContext>(reply => reply.ReplyTo);
        services.AddSagaMessage<FundsTransferRejected, TopupMerchantDbContext>(reply => reply.ReplyTo);

        // ---- Merchant replies that are not for a Saga: wallet opened, settlement posted or refused ----
        services.AddIntegrationEventHandler<MerchantWalletOpened, MerchantReplyHandlers>();
        services.AddIntegrationEventHandler<MerchantSettled, MerchantReplyHandlers>();
        services.AddIntegrationEventHandler<FundsTransferRejected, MerchantReplyHandlers>();

        // ---- Networks: one resilience pipeline per network ----
        services.AddSingleton<TelcoMetrics>();
        services.AddSingleton<TelcoResilience>();
        services.AddSingleton<INetworkAvailability>(provider => provider.GetRequiredService<TelcoResilience>());
        services.AddHttpClient<ITelcoGateway, TelcoGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<TelcoOptions>();
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<TopupMerchantDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISagaLists, SagaLists>();
        services.AddScoped<IMerchantStore, MerchantStore>();

        // ---- Use cases and the nightly settlement job (M8.5) ----
        services.AddScoped<TopUpService>();
        services.AddScoped<MerchantService>();
        services.AddSingleton<MerchantSettlementService>();
        services.AddHostedService(provider => provider.GetRequiredService<MerchantSettlementService>());

        services.AddHealthChecks()
            .AddDbContextCheck<TopupMerchantDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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
