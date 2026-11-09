using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WalletPK.Gateway.IntegrationTests.Infrastructure;
using WalletPK.Web.Correlation;
using WalletPK.Web.Identity;

namespace WalletPK.Gateway.IntegrationTests;

public class GatewayRoutingAndAuthTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private HttpClient ClientWithToken(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Landing_page_lists_the_routes()
    {
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>(new Uri("/", UriKind.Relative), Cancel);

        info.GetProperty("service").GetString().ShouldBe("WalletPK API Gateway");
        info.GetProperty("routes").GetArrayLength().ShouldBe(16);
    }

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_routes_are_forwarded_without_a_token()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/api/identity/auth/login", UriKind.Relative), new { phone = "+923001234567" }, Cancel);
        var echo = await response.Content.ReadFromJsonAsync<EchoResponse>(Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        echo.ShouldNotBeNull();
        echo.Path.ShouldBe("/api/identity/auth/login");
        echo.Method.ShouldBe("POST");
        echo.Header(IdentityHeaders.UserId).ShouldBeNull();
    }

    [Theory]
    [InlineData("/api/wallets/me")]
    [InlineData("/api/payments/transfers")]
    [InlineData("/api/bills/pay")]
    [InlineData("/api/identity/profile")]
    public async Task Protected_routes_reject_requests_without_a_token(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
    }

    [Fact]
    public async Task Valid_tokens_are_forwarded_with_verified_identity_headers()
    {
        using var client = ClientWithToken(factory.CreateToken("user-123", kycTier: "L2", deviceId: "pixel-8"));
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "corr-gw-1");

        var echo = await client.GetFromJsonAsync<EchoResponse>(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        echo.ShouldNotBeNull();
        echo.Path.ShouldBe("/api/wallets/me");
        echo.Header(IdentityHeaders.UserId).ShouldBe("user-123");
        echo.Header(IdentityHeaders.DeviceId).ShouldBe("pixel-8");
        echo.Header(IdentityHeaders.KycTier).ShouldBe("L2");
        echo.Header(IdentityHeaders.Roles).ShouldBe("customer");
        echo.Header("Authorization").ShouldNotBeNull().ShouldStartWith("Bearer ");
        echo.Header(CorrelationIdMiddleware.HeaderName).ShouldBe("corr-gw-1");
    }

    [Fact]
    public async Task The_gateway_creates_and_forwards_a_correlation_id_when_the_client_sends_none()
    {
        using var client = ClientWithToken(factory.CreateToken("user-1"));

        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);
        var echo = await response.Content.ReadFromJsonAsync<EchoResponse>(Cancel);

        var returned = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        echo.ShouldNotBeNull();
        echo.Header(CorrelationIdMiddleware.HeaderName).ShouldBe(returned);
    }

    [Fact]
    public async Task Clients_cannot_spoof_identity_headers()
    {
        using var client = ClientWithToken(factory.CreateToken("real-user"));
        client.DefaultRequestHeaders.Add(IdentityHeaders.UserId, "attacker");
        client.DefaultRequestHeaders.Add(IdentityHeaders.KycTier, "L2");

        var echo = await client.GetFromJsonAsync<EchoResponse>(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        echo.ShouldNotBeNull();
        echo.Header(IdentityHeaders.UserId).ShouldBe("real-user");
        echo.Header(IdentityHeaders.KycTier).ShouldBe("L1");
    }

    [Fact]
    public async Task Expired_tokens_are_rejected()
    {
        using var client = ClientWithToken(factory.CreateToken("user-1", expires: DateTime.UtcNow.AddMinutes(-5)));

        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_for_another_audience_are_rejected()
    {
        using var client = ClientWithToken(factory.CreateToken("user-1", audience: "some-other-api"));

        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_signed_with_an_unknown_key_are_rejected()
    {
        using var otherRsa = System.Security.Cryptography.RSA.Create(2048);
        var forgedKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(otherRsa) { KeyId = "attacker-key" };
        using var client = ClientWithToken(factory.CreateToken("user-1", signingKey: forgedKey));

        using var response = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    [InlineData("merchant", HttpStatusCode.Forbidden)]
    [InlineData("ops", HttpStatusCode.OK)]
    [InlineData("compliance", HttpStatusCode.OK)]
    public async Task Back_office_routes_require_a_staff_role(string role, HttpStatusCode expected)
    {
        using var client = ClientWithToken(factory.CreateToken("user-1", role: role));

        using var response = await client.GetAsync(new Uri("/api/fraud/alerts", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("customer", HttpStatusCode.Forbidden)]
    [InlineData("finance", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    public async Task Ledger_checks_are_for_staff_while_wallets_are_for_everyone(string role, HttpStatusCode expected)
    {
        using var client = ClientWithToken(factory.CreateToken("user-1", role: role));

        using var invariants = await client.GetAsync(new Uri("/api/ledger/invariants", UriKind.Relative), Cancel);
        using var wallet = await client.GetAsync(new Uri("/api/wallets/me", UriKind.Relative), Cancel);

        invariants.StatusCode.ShouldBe(expected);
        wallet.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_routes_return_a_problem_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Cors_allows_the_web_app_origin_only()
    {
        using var client = factory.CreateClient();

        using var allowed = await client.SendAsync(Preflight(GatewayFactory.AllowedOrigin), Cancel);
        using var blocked = await client.SendAsync(Preflight("https://evil.example"), Cancel);

        allowed.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(GatewayFactory.AllowedOrigin);
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
        blocked.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/wallets/me", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }
}
