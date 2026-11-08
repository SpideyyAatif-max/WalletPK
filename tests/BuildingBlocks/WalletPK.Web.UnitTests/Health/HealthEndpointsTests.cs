using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WalletPK.Web.Health;
using WalletPK.Web.UnitTests.Infrastructure;

namespace WalletPK.Web.UnitTests.Health;

public class HealthEndpointsTests
{
    [Theory]
    [InlineData(HealthEndpoints.LivePath)]
    [InlineData(HealthEndpoints.ReadyPath)]
    public async Task Health_endpoints_report_healthy_as_json(string path)
    {
        await using var app = await TestApp.StartAsync(_ => { });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Liveness_runs_only_the_self_check()
    {
        await using var app = await TestApp.StartAsync(_ => { });
        using var client = app.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>(new Uri(HealthEndpoints.LivePath, UriKind.Relative), TestContext.Current.CancellationToken);

        var checks = body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? string.Empty).ToArray();
        checks.ShouldBe(new[] { "self" });
    }
}
