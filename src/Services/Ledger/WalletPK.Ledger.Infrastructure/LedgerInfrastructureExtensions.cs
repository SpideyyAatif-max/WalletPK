using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Application.Events;
using WalletPK.Ledger.Application.Posting;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Outbox;
using WalletPK.Web.Correlation;
using WalletPK.Web.Health;

namespace WalletPK.Ledger.Infrastructure;

public static class LedgerInfrastructureExtensions
{
    public const string DatabaseConnectionName = "LedgerDb";

    /// <summary>Registers the Ledger use cases and their adapters.</summary>
    public static IServiceCollection AddLedgerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

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

        // ---- Use cases ----
        services.AddScoped<PostingService>();

        // ---- Readiness: ready only when the database is reachable ----
        services.AddHealthChecks()
            .AddDbContextCheck<LedgerDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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
