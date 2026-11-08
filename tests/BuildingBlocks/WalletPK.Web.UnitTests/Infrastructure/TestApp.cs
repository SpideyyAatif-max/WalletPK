using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace WalletPK.Web.UnitTests.Infrastructure;

/// <summary>Starts a real in-memory ASP.NET Core app wired with the WalletPK web defaults.</summary>
internal static class TestApp
{
    public static async Task<WebApplication> StartAsync(Action<WebApplication> mapEndpoints, string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddWalletPkWeb();

        var app = builder.Build();
        app.UseWalletPkWeb();
        mapEndpoints(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
