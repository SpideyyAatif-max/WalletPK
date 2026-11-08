using Serilog.Core;
using Serilog.Events;
using WalletPK.Web.Correlation;

namespace WalletPK.Observability;

/// <summary>
/// Stamps the current correlation ID on every log event, including events written directly
/// through Serilog (such as the per-request summary line) and, later, by Kafka consumers.
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public const string PropertyName = "CorrelationId";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var correlationId = CorrelationContext.Current;
        if (correlationId is not null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, correlationId));
        }
    }
}
