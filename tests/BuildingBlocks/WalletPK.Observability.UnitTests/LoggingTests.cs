using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using WalletPK.Observability.UnitTests.Infrastructure;
using WalletPK.Web.Correlation;

namespace WalletPK.Observability.UnitTests;

public class LoggingTests
{
    private const string TransferTemplate = "Transfer requested for {Amount}";

    private static Task<WebApplication> StartAppAsync(CollectingSink sink) =>
        ObservabilityTestApp.StartAsync(
            options => options.ConfigureLogging = logger => logger.WriteTo.Sink(sink),
            app => app.MapGet("/transfer", (ILogger<LoggingTests> logger) =>
            {
                logger.LogInformation(TransferTemplate, 500);
                return "ok";
            }));

    [Fact]
    public async Task Application_logs_carry_the_correlation_id_and_service_name()
    {
        var sink = new CollectingSink();
        await using var app = await StartAppAsync(sink);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "corr-777");

        using var response = await client.GetAsync(new Uri("/transfer", UriKind.Relative), TestContext.Current.CancellationToken);

        var logEvent = sink.Events.Single(e => e.MessageTemplate.Text == TransferTemplate);
        CollectingSink.Property(logEvent, "CorrelationId").ShouldBe("corr-777");
        CollectingSink.Property(logEvent, "Service").ShouldBe("test-service");
        CollectingSink.Property(logEvent, "Amount").ShouldBe(500);
    }

    [Fact]
    public async Task Each_request_gets_one_summary_line_with_status_and_correlation_id()
    {
        var sink = new CollectingSink();
        await using var app = await StartAppAsync(sink);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "corr-888");

        using var response = await client.GetAsync(new Uri("/transfer", UriKind.Relative), TestContext.Current.CancellationToken);
        await ObservabilityTestApp.WaitUntilAsync(() => sink.Events.Any(IsRequestSummary));

        var summary = sink.Events.Single(IsRequestSummary);
        CollectingSink.Property(summary, "RequestPath").ShouldBe("/transfer");
        CollectingSink.Property(summary, "StatusCode").ShouldBe(200);
        CollectingSink.Property(summary, "CorrelationId").ShouldBe("corr-888");
    }

    [Fact]
    public async Task Health_checks_do_not_flood_the_logs()
    {
        var sink = new CollectingSink();
        await using var app = await StartAppAsync(sink);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        sink.Events.ShouldNotContain(e => IsRequestSummary(e));
    }

    private static bool IsRequestSummary(Serilog.Events.LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.StartsWith("HTTP {RequestMethod}", StringComparison.Ordinal);
}
