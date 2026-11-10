using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.Dispatching;

/// <summary>
/// Calls every registered <see cref="IIntegrationEventHandler{TEvent}"/> for an event, in
/// registration order. Handlers are resolved from the current scope, so they share its DbContext
/// (and therefore its transaction) with the inbox.
/// </summary>
public sealed class IntegrationEventDispatcher(IServiceProvider services)
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IntegrationEvent, EventContext, CancellationToken, Task<int>>> _invokers = new();

    private static readonly MethodInfo _dispatchTyped =
        typeof(IntegrationEventDispatcher).GetMethod(nameof(DispatchTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Returns how many handlers ran (0 when nobody here is interested in the event).</summary>
    public Task<int> DispatchAsync(IntegrationEvent integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);

        var invoker = _invokers.GetOrAdd(integrationEvent.GetType(), type =>
            _dispatchTyped.MakeGenericMethod(type)
                .CreateDelegate<Func<IServiceProvider, IntegrationEvent, EventContext, CancellationToken, Task<int>>>());
        return invoker(services, integrationEvent, context, cancellationToken);
    }

    private static async Task<int> DispatchTypedAsync<TEvent>(
        IServiceProvider services, IntegrationEvent integrationEvent, EventContext context, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        var handled = 0;
        foreach (var handler in services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync((TEvent)integrationEvent, context, cancellationToken);
            handled++;
        }

        return handled;
    }
}

public static class IntegrationEventHandlerRegistration
{
    /// <summary>Registers a scoped handler for <typeparamref name="TEvent"/>.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        services.TryAddScoped<IntegrationEventDispatcher>();
        return services;
    }
}
