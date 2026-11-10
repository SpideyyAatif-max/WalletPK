using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WalletPK.Common.Correlation;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Application.Events;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Invariants;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Idempotency;
using WalletPK.Ledger.Infrastructure.Invariants;
using WalletPK.Ledger.Infrastructure.Limits;
using WalletPK.Ledger.Infrastructure.Maintenance;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.Infrastructure.Redis;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Persistence.Outbox;
using WalletPK.Web.Health;
using WalletPK.Web.Idempotency;

namespace WalletPK.Ledger.Infrastructure;

public static class LedgerInfrastructureExtensions
{
    /// <summary>The Ledger's Kafka consumer group (and inbox consumer name).</summary>
    public const string LedgerConsumerGroup = "ledger";

    public const string DatabaseConnectionName = "LedgerDb";
    public const string RedisConnectionName = "Redis";

    /// <summary>Registers the Ledger use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddLedgerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // ---- Options (validated at startup) ----
        services.AddOptions<WalletLockOptions>()
            .BindConfiguration(WalletLockOptions.SectionName)
            .Validate(options => options.TimeToLiveMilliseconds >= 1_000 && options.WaitMilliseconds >= 0, "Ledger:Locks: TimeToLiveMilliseconds >= 1000, WaitMilliseconds >= 0.")
            .ValidateOnStart();
        services.AddOptions<IdempotencyOptions>()
            .BindConfiguration(IdempotencyOptions.SectionName)
            .Validate(options => options.RetentionHours >= 1 && options.InProgressTimeoutSeconds >= 5, "Ledger:Idempotency: RetentionHours >= 1, InProgressTimeoutSeconds >= 5.")
            .ValidateOnStart();
        services.AddOptions<MaintenanceOptions>()
            .BindConfiguration(MaintenanceOptions.SectionName)
            .Validate(options => options.IntervalSeconds >= 1 && options.HoldBatchSize >= 1, "Ledger:Maintenance: IntervalSeconds >= 1, HoldBatchSize >= 1.")
            .ValidateOnStart();
        services.AddOptions<FeeOptions>()
            .BindConfiguration(FeeOptions.SectionName)
            .Validate(options => options.IsValid(), "Fees: bands must be ordered by UpToRupees, only the last may be open-ended, values must not be negative, and each schedule must name a JournalType.")
            .ValidateOnStart();
        services.AddOptions<IdentityGrpcOptions>()
            .BindConfiguration(IdentityGrpcOptions.SectionName)
            .Validate(options => options.Address is { IsAbsoluteUri: true } && options.TimeoutMilliseconds >= 100, "Services:IdentityGrpc: Address must be an absolute URI and TimeoutMilliseconds >= 100.")
            .ValidateOnStart();
        services.AddOptions<AuditOptions>()
            .BindConfiguration(AuditOptions.SectionName)
            .Validate(options => options.SealIntervalMilliseconds >= 100 && options.BatchSize >= 1 && options.MaxUnsealedAgeSeconds >= 10, "Ledger:Audit: SealIntervalMilliseconds >= 100, BatchSize >= 1, MaxUnsealedAgeSeconds >= 10.")
            .ValidateOnStart();
        services.AddOptions<InvariantOptions>()
            .BindConfiguration(InvariantOptions.SectionName)
            .Validate(options => options.IntervalMinutes >= 1, "Ledger:Invariants: IntervalMinutes >= 1.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AuditOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<InvariantOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<WalletLockOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FeeOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<IdentityGrpcOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<IdempotencyOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MaintenanceOptions>>().Value);

        // ---- Integration events and the outbox writer ----
        // Events the Ledger publishes (ledger.*) and consumes (identity.*).
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(WalletPK.Contracts.Ledger.JournalPosted).Assembly, LedgerIntegrationEvents.ContractsNamespace)
            .RegisterFromAssembly(typeof(WalletPK.Contracts.Identity.UserRegistered).Assembly, typeof(WalletPK.Contracts.Identity.UserRegistered).Namespace));
        services.AddSingleton<JsonEventSerializer>();
        services.AddDomainEventOutbox<LedgerIntegrationEventMapper>(() => CorrelationContext.Current);

        // ---- Kafka (M4): outbox relay out, inbox in; new users get a wallet automatically ----
        services.AddWalletPkKafka(clientId: "ledger");
        services.AddOutboxRelay<LedgerDbContext>();
        services.AddInbox<LedgerDbContext>();
        services.AddIntegrationEventHandler<WalletPK.Contracts.Identity.UserRegistered, UserRegisteredHandler>();
        services.AddKafkaConsumer(LedgerConsumerGroup, KafkaTopics.IdentityEvents);

        // ---- Audit log: a row for every money-relevant change, in the same transaction (M3.10) ----
        services.AddHttpContextAccessor();
        services.AddSingleton(provider => new LedgerAuditInterceptor(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IHttpContextAccessor>(),
            () => CorrelationContext.Current));

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<LedgerDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                provider.GetRequiredService<DomainEventOutboxInterceptor>(),
                provider.GetRequiredService<LedgerAuditInterceptor>()));
        services.AddScoped<ILedgerUnitOfWork>(provider => provider.GetRequiredService<LedgerDbContext>());
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IJournalRepository, JournalRepository>();
        services.AddScoped<IHoldRepository, HoldRepository>();
        services.AddScoped<ILedgerQueries, LedgerQueries>();

        // ---- Redis: wallet locks and the idempotency fast path ----
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            ConnectionMultiplexer.Connect(RequiredConnectionString(provider, RedisConnectionName)));
        services.AddSingleton<IAccountLockService, RedisAccountLockService>();
        services.AddScoped<IIdempotencyStore, LedgerIdempotencyStore>();

        // ---- Identity's limits API (gRPC, internal port) ----
        services.AddGrpcClient<WalletPK.Contracts.Identity.V1.Limits.LimitsClient>((provider, options) =>
            options.Address = provider.GetRequiredService<IdentityGrpcOptions>().Address);
        services.AddScoped<ILimitsGateway, IdentityLimitsGateway>();

        // ---- Use cases ----
        services.AddScoped<WalletConcurrency>();
        services.AddScoped<PostingService>();
        services.AddScoped<HoldService>();
        services.AddSingleton<FeeEngine>();
        services.AddScoped<WalletService>();

        // ---- Audit chain and invariants (M3.10, M3.11) ----
        services.AddScoped<AuditSealer>();
        services.AddScoped<AuditChainVerifier>();
        services.AddScoped<ILedgerInvariantChecker, LedgerInvariantChecker>();
        services.AddMetrics();
        services.AddSingleton<LedgerMetrics>();

        // ---- Background: hold expiry sweeper, idempotency cleanup, audit sealer, invariant monitor ----
        services.AddHostedService<LedgerMaintenanceService>();
        services.AddHostedService<AuditSealerService>();
        services.AddHostedService<InvariantMonitorService>();

        // ---- Readiness: ready only when the database is reachable ----
        services.AddHealthChecks()
            .AddDbContextCheck<LedgerDbContext>("postgres", tags: [HealthCheckTags.Ready])
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
