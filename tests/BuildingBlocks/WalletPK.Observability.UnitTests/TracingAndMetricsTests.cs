using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using WalletPK.Observability.UnitTests.Infrastructure;

namespace WalletPK.Observability.UnitTests;

public class TracingAndMetricsTests
{
    private static readonly ActivitySource _ledgerSource = new(WalletPkTelemetry.NameFor("Ledger"));

    [Fact]
    public async Task Requests_and_custom_spans_are_traced_but_health_checks_are_not()
    {
        var exported = new List<Activity>();
        await using var app = await ObservabilityTestApp.StartAsync(
            options => options.ConfigureTracing = tracing => tracing.AddInMemoryExporter(exported),
            app => app.MapGet("/post", () =>
            {
                using var activity = _ledgerSource.StartActivity("ledger.post_journal");
                return "posted";
            }));
        using var client = app.GetTestClient();

        using var post = await client.GetAsync(new Uri("/post", UriKind.Relative), TestContext.Current.CancellationToken);
        using var health = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        await ObservabilityTestApp.WaitUntilAsync(() => exported.ToArray().Any(a => a.Kind == ActivityKind.Server));
        app.Services.GetRequiredService<TracerProvider>().ForceFlush();

        var spans = exported.ToArray();
        spans.ShouldContain(a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("url.path"), "/post"));
        spans.ShouldContain(a => a.OperationName == "ledger.post_journal");
        spans.ShouldNotContain(a => Equals(a.GetTagItem("url.path"), "/health/live"));
    }

    [Fact]
    public async Task Custom_walletpk_metrics_are_collected()
    {
        var exported = new List<Metric>();
        await using var app = await ObservabilityTestApp.StartAsync(
            options => options.ConfigureMetrics = metrics => metrics.AddInMemoryExporter(exported),
            _ => { });

        using var meter = new Meter(WalletPkTelemetry.NameFor("Ledger"));
        var postings = meter.CreateCounter<long>("walletpk.ledger.postings");
        postings.Add(3);

        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        exported.ShouldContain(m => m.Name == "walletpk.ledger.postings");
    }

    [Fact]
    public void Telemetry_names_follow_the_walletpk_convention()
    {
        WalletPkTelemetry.NameFor("Payments").ShouldBe("WalletPK.Payments");
        WalletPkTelemetry.Wildcard.ShouldBe("WalletPK.*");
        Should.Throw<ArgumentException>(() => WalletPkTelemetry.NameFor(" "));
    }
}
