using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Agents.Application;
using WalletPK.Agents.Infrastructure.Payouts;
using WalletPK.Agents.Infrastructure.Persistence;
using WalletPK.Contracts.Agents;
using WalletPK.Contracts.Ledger;
using WalletPK.Messaging;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence;
using WalletPK.Sagas;
using WalletPK.Web.Health;

namespace WalletPK.Agents.Infrastructure;

public static class AgentsInfrastructureExtensions
{
    /// <summary>The Kafka consumer group (and inbox consumer name).</summary>
    public const string ConsumerGroup = "agents";

    public const string DatabaseConnectionName = "AgentsDb";

    /// <summary>Registers the agent use cases and their adapters. Settings are read lazily from configuration.</summary>
    public static IServiceCollection AddAgentServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();

        // ---- Options (validated at startup) ----
        services.AddOptions<AgentOptions>()
            .BindConfiguration(AgentOptions.SectionName)
            .Validate(options => options.IsValid(), "Agents: MinimumKycTier (L0, L1 or L2) and CashOutCodeKey (at least 32 characters).")
            .ValidateOnStart();
        services.AddOptions<AgentTransactionOptions>()
            .BindConfiguration(AgentTransactionOptions.SectionName)
            .Validate(options => options.IsValid(), "Agents:Transactions: positive LedgerReplyTimeoutSeconds and MaxLedgerResends.")
            .ValidateOnStart();
        services.AddOptions<CommissionPayoutOptions>()
            .BindConfiguration(CommissionPayoutOptions.SectionName)
            .Validate(options => options.IsValid(), "Agents:Payouts: positive PollSeconds and ResendMinutes.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AgentOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AgentTransactionOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<CommissionPayoutOptions>>().Value);

        // ---- Events and commands: Ledger agent commands and replies, agents.* events ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(OpenAgentWallet).Assembly, typeof(OpenAgentWallet).Namespace)
            .RegisterFromAssembly(typeof(AgentTransactionChanged).Assembly, typeof(AgentTransactionChanged).Namespace));
        services.AddSingleton<JsonEventSerializer>();

        // ---- Kafka: the outbox out (ledger.commands, agents.events), the inbox in (ledger.replies) ----
        services.AddWalletPkKafka(clientId: "agents");
        services.AddOutboxRelay<AgentsDbContext>();
        services.AddInbox<AgentsDbContext>();
        services.AddKafkaConsumer(ConsumerGroup, KafkaTopics.LedgerReplies);

        // ---- The agent transaction Saga: cash-in, cash-out and float moves, each one Ledger command ----
        services.AddSagas<AgentsDbContext>();
        services.AddSaga<AgentTransactionSaga>();
        services.AddSagaMessage<AgentCashedIn, AgentsDbContext>(reply => reply.ReplyTo);
        services.AddSagaMessage<AgentCashedOut, AgentsDbContext>(reply => reply.ReplyTo);
        services.AddSagaMessage<AgentFloatMoved, AgentsDbContext>(reply => reply.ReplyTo);
        services.AddSagaMessage<FundsTransferRejected, AgentsDbContext>(reply => reply.ReplyTo);

        // ---- Replies that are not for a Saga: float opened, commission payout posted or refused ----
        services.AddIntegrationEventHandler<AgentWalletOpened, AgentReplyHandlers>();
        services.AddIntegrationEventHandler<AgentCommissionPaid, AgentReplyHandlers>();
        services.AddIntegrationEventHandler<FundsTransferRejected, AgentReplyHandlers>();

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<AgentsDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ISagaLists, SagaLists>();
        services.AddScoped<IAgentStore, AgentStore>();

        // ---- Use cases and the nightly commission payout job (M9.4) ----
        services.AddScoped<AgentService>();
        services.AddSingleton<CommissionPayoutService>();
        services.AddHostedService(provider => provider.GetRequiredService<CommissionPayoutService>());

        services.AddHealthChecks()
            .AddDbContextCheck<AgentsDbContext>("postgres", tags: [HealthCheckTags.Ready]);

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
