using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Concurrency;
using WalletPK.Ledger.Application.Events;
using WalletPK.Ledger.Application.Fees;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Application.Wallets;
using WalletPK.Ledger.Infrastructure.Idempotency;
using WalletPK.Ledger.Infrastructure.Limits;
using WalletPK.Ledger.Infrastructure.Maintenance;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Ledger.Infrastructure.Redis;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;
using WalletPK.Web.Correlation;
using WalletPK.Web.Health;
using WalletPK.Web.Idempotency;

namespace WalletPK.Ledger.Infrastructure;

public static class LedgerInfrastructureExtensions
{
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
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<WalletLockOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FeeOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<IdentityGrpcOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<IdempotencyOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MaintenanceOptions>>().Value);

        // ---- Integration events and the outbox writer ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(WalletPK.Contracts.Ledger.JournalPosted).Assembly, LedgerIntegrationEvents.ContractsNamespace));
        services.AddSingleton<JsonEventSerializer>();
        services.AddDomainEventOutbox<LedgerIntegrationEventMapper>(() => CorrelationContext.Current);

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<LedgerDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(provider.GetRequiredService<DomainEventOutboxInterceptor>()));
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
        services.AddSingleton<WalletConcurrency>();
        services.AddScoped<PostingService>();
        services.AddScoped<HoldService>();
        services.AddSingleton<FeeEngine>();
        services.AddScoped<WalletService>();

        // ---- Background: hold expiry sweeper and idempotency cleanup ----
        services.AddHostedService<LedgerMaintenanceService>();

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
