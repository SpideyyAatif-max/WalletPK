using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace WalletPK.Observability.UnitTests.Infrastructure;

/// <summary>Captures Serilog events in memory so tests can inspect them.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public static object? Property(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;
}
