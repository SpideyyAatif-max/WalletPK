namespace WalletPK.Web.Correlation;

/// <summary>
/// Adds the current correlation ID to outgoing HTTP calls (service-to-service, mock bank calls),
/// so one ID ties together everything a single user action caused.
/// Register with <c>services.AddHttpClient(...).AddHttpMessageHandler&lt;CorrelationIdDelegatingHandler&gt;()</c>.
/// </summary>
public sealed class CorrelationIdDelegatingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = CorrelationContext.Current;
        if (!string.IsNullOrEmpty(correlationId) && !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
