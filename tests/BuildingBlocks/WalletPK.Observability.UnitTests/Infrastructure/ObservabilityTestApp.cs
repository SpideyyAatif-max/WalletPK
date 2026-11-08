using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using WalletPK.Web;

namespace WalletPK.Observability.UnitTests.Infrastructure;

/// <summary>An in-memory app with WalletPK web + observability defaults and no external exporters.</summary>
internal static class ObservabilityTestApp
{
    public static async Task<WebApplication> StartAsync(Action<ObservabilityOptions> configure, Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddWalletPkWeb();
        builder.AddWalletPkObservability("test-service", options =>
        {
            options.WriteToConsole = false;
            options.WriteToSeq = false;
            options.ExportTraces = false;
            options.ExportMetrics = false;
            configure(options);
        });

        var app = builder.Build();
        app.UseWalletPkWeb();
        app.UseWalletPkObservability();
        mapEndpoints(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>Waits until a condition holds (telemetry is written just after the response is sent).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
