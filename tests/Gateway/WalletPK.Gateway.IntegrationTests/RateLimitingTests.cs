using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Gateway.IntegrationTests.Infrastructure;

namespace WalletPK.Gateway.IntegrationTests;

public class RateLimitingTests(RateLimitedGatewayFactory factory) : IClassFixture<RateLimitedGatewayFactory>
{
    [Fact]
    public async Task Login_attempts_beyond_the_limit_get_429_with_retry_after()
    {
        using var client = factory.CreateClient();
        var cancel = TestContext.Current.CancellationToken;
        var login = new Uri("/api/identity/auth/login", UriKind.Relative);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var allowed = await client.PostAsJsonAsync(login, new { phone = "+923001234567", attempt }, cancel);
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt} should be allowed");
        }

        using var blocked = await client.PostAsJsonAsync(login, new { phone = "+923001234567", attempt = 4 }, cancel);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>(cancel);

        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.ShouldNotBeNull();
        problem.GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");
    }

    [Fact]
    public async Task Health_checks_are_never_rate_limited()
    {
        using var client = factory.CreateClient();
        var cancel = TestContext.Current.CancellationToken;

        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), cancel);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
