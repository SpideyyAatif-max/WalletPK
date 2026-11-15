using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Events;

namespace WalletPK.Sagas;

public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Runs Sagas on <typeparamref name="TDbContext"/> (whose model calls AddSagas and has the outbox):
    /// the engine, the reader, and the background timer loop ("Sagas:Timers").
    /// Needs a JsonEventSerializer and a TimeProvider. Register definitions with AddSaga.
    /// </summary>
    public static IServiceCollection AddSagas<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SagaTimerOptions>()
            .BindConfiguration(SagaTimerOptions.SectionName)
            .Validate(options => options.IsValid(), "Sagas:Timers: BatchSize 1-1000, MaxParallelism 1-256, PollIntervalMilliseconds >= 10, StepTimeoutSeconds >= 1, LeaseSeconds > StepTimeoutSeconds, RetryDelaySeconds >= 1.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<SagaTimerOptions>>().Value);
        services.TryAddSingleton<SagaRegistry>();
        services.AddScoped<SagaEngine<TDbContext>>();
        services.AddScoped<ISagaEngine>(provider => provider.GetRequiredService<SagaEngine<TDbContext>>());
        services.AddScoped<SagaReader<TDbContext>>();
        services.AddScoped<ISagaReader>(provider => provider.GetRequiredService<SagaReader<TDbContext>>());
        services.AddSingleton<SagaTimerRunner<TDbContext>>();
        services.AddHostedService<SagaTimerService<TDbContext>>();
        return services;
    }

    /// <summary>Registers a Saga definition (a singleton: definitions hold no per-Saga state).</summary>
    public static IServiceCollection AddSaga<TDefinition>(this IServiceCollection services)
        where TDefinition : class, ISagaDefinition
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TDefinition>();
        services.AddSingleton<ISagaDefinition>(provider => provider.GetRequiredService<TDefinition>());
        return services;
    }

    /// <summary>
    /// Delivers consumed <typeparamref name="TMessage"/> events (through the inbox) to the Saga
    /// <paramref name="sagaIdOf"/> names. Events for unknown Sagas are ignored (logged).
    /// </summary>
    public static IServiceCollection AddSagaMessage<TMessage, TDbContext>(this IServiceCollection services, Func<TMessage, Guid> sagaIdOf)
        where TMessage : IntegrationEvent
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sagaIdOf);
        services.AddSingleton(new SagaMessageRoute<TMessage>(sagaIdOf));
        return services.AddIntegrationEventHandler<TMessage, SagaMessageHandler<TMessage, TDbContext>>();
    }
}

internal sealed record SagaMessageRoute<TMessage>(Func<TMessage, Guid> SagaIdOf);

internal sealed partial class SagaMessageHandler<TMessage, TDbContext>(
    SagaMessageRoute<TMessage> route,
    SagaEngine<TDbContext> engine,
    ILogger<SagaMessageHandler<TMessage, TDbContext>> logger) : IIntegrationEventHandler<TMessage>
    where TMessage : IntegrationEvent
    where TDbContext : DbContext
{
    public async Task HandleAsync(TMessage integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        var sagaId = route.SagaIdOf(integrationEvent);
        var outcome = await engine.HandleAsync(sagaId, integrationEvent, context.EventId.ToString(), cancellationToken);
        if (outcome == SagaHandleOutcome.NotFound)
        {
            // Normal on shared reply topics: other services' Sagas get replies there too.
            LogUnknownSaga(logger, typeof(TMessage).Name, sagaId);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Message} for a Saga this service does not have ({SagaId}); ignored")]
    private static partial void LogUnknownSaga(ILogger logger, string message, Guid sagaId);
}
