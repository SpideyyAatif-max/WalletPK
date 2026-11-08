using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.Serialization;

/// <summary>
/// Knows every integration event type a service publishes or consumes, keyed by stable name and
/// version. Built once at startup; misconfigured events (missing attribute, bad name, unknown topic,
/// duplicate name) fail fast here instead of at the first message.
/// </summary>
public sealed partial class IntegrationEventRegistry
{
    private readonly Dictionary<(string Name, int Version), Type> _typesByName = new();
    private readonly Dictionary<Type, IntegrationEventAttribute> _metadataByType = new();

    /// <summary>Registered event types.</summary>
    public IReadOnlyCollection<Type> EventTypes => _metadataByType.Keys;

    public IntegrationEventRegistry Register<TEvent>()
        where TEvent : IntegrationEvent => Register(typeof(TEvent));

    public IntegrationEventRegistry Register(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        if (eventType.IsAbstract || !typeof(IntegrationEvent).IsAssignableFrom(eventType))
        {
            throw new InvalidOperationException(
                $"{eventType.Name} must be a concrete type deriving from {nameof(IntegrationEvent)}.");
        }

        var metadata = eventType.GetCustomAttribute<IntegrationEventAttribute>()
            ?? throw new InvalidOperationException(
                $"{eventType.Name} must be marked with [IntegrationEvent(\"<area>.<fact>\", KafkaTopics.<Topic>)].");

        if (!EventNamePattern().IsMatch(metadata.Name))
        {
            throw new InvalidOperationException(
                $"Event name '{metadata.Name}' on {eventType.Name} must look like 'ledger.money_transferred' (lowercase, dot-separated).");
        }

        if (!KafkaTopics.All.Contains(metadata.Topic))
        {
            throw new InvalidOperationException(
                $"Topic '{metadata.Topic}' on {eventType.Name} is not a WalletPK topic. Use a {nameof(KafkaTopics)} constant.");
        }

        if (metadata.Version < 1)
        {
            throw new InvalidOperationException($"Version of {eventType.Name} must be 1 or higher.");
        }

        var key = (metadata.Name, metadata.Version);
        if (_typesByName.TryGetValue(key, out var existing) && existing != eventType)
        {
            throw new InvalidOperationException(
                $"Both {existing.Name} and {eventType.Name} claim event '{metadata.Name}' version {metadata.Version}.");
        }

        _typesByName[key] = eventType;
        _metadataByType[eventType] = metadata;
        return this;
    }

    /// <summary>
    /// Registers every concrete integration event in an assembly, optionally only those whose
    /// namespace starts with <paramref name="namespacePrefix"/>.
    /// </summary>
    public IntegrationEventRegistry RegisterFromAssembly(Assembly assembly, string? namespacePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var eventTypes = assembly.GetTypes().Where(type =>
            !type.IsAbstract &&
            typeof(IntegrationEvent).IsAssignableFrom(type) &&
            type.GetCustomAttribute<IntegrationEventAttribute>() is not null &&
            (namespacePrefix is null || (type.Namespace?.StartsWith(namespacePrefix, StringComparison.Ordinal) ?? false)));

        foreach (var eventType in eventTypes)
        {
            Register(eventType);
        }

        return this;
    }

    /// <summary>Name, topic and version of a registered event type.</summary>
    public IntegrationEventAttribute GetMetadata(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _metadataByType.TryGetValue(eventType, out var metadata)
            ? metadata
            : throw new InvalidOperationException($"{eventType.Name} is not registered in the {nameof(IntegrationEventRegistry)}.");
    }

    /// <summary>Finds the C# type for an incoming event name and version.</summary>
    public bool TryGetType(string name, int version, [NotNullWhen(true)] out Type? eventType) =>
        _typesByName.TryGetValue((name, version), out eventType);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EventNamePattern();
}
