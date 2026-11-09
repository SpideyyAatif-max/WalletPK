using Microsoft.Extensions.DependencyInjection;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Persistence.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox writer for a service: its domain-to-integration event mapper and the
    /// <see cref="DomainEventOutboxInterceptor"/> (add it to the DbContext with AddInterceptors).
    /// Requires a <see cref="JsonEventSerializer"/> and a <see cref="TimeProvider"/> in the container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="correlationIdAccessor">Returns the current correlation ID (stored on each outbox row).</param>
    public static IServiceCollection AddDomainEventOutbox<TMapper>(this IServiceCollection services, Func<string?> correlationIdAccessor)
        where TMapper : class, IIntegrationEventMapper
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(correlationIdAccessor);

        services.AddSingleton<IIntegrationEventMapper, TMapper>();
        services.AddSingleton(provider => new DomainEventOutboxInterceptor(
            provider.GetRequiredService<IIntegrationEventMapper>(),
            provider.GetRequiredService<JsonEventSerializer>(),
            provider.GetRequiredService<TimeProvider>(),
            correlationIdAccessor));

        return services;
    }
}
