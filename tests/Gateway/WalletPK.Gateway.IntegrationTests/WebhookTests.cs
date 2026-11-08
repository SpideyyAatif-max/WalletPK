using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WalletPK.Gateway.IntegrationTests.Infrastructure;
using WalletPK.Web.Security;

namespace WalletPK.Gateway.IntegrationTests;

public class WebhookTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private const string CallbackPath = "/webhooks/raast/transfers/callback";
    private const string Body = """{"transferId":"rt-1001","status":"SETTLED","amount":{"minorUnits":250000,"currency":"PKR"}}""";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static HttpRequestMessage Callback(string path, string body, long? timestamp, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (timestamp is not null)
        {
            request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (signature is not null)
        {
            request.Headers.Add(WebhookSignature.SignatureHeader, signature);
        }

        return request;
    }

    private static string Sign(long timestamp, string body, string secret = GatewayFactory.RaastSecret) =>
        WebhookSignature.Compute(secret, timestamp, Encoding.UTF8.GetBytes(body));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Correctly_signed_callbacks_are_forwarded_with_the_body_intact()
    {
        using var client = factory.CreateClient();
        var timestamp = Now;

        using var response = await client.SendAsync(Callback(CallbackPath, Body, timestamp, Sign(timestamp, Body)), Cancel);
        var echo = await response.Content.ReadFromJsonAsync<EchoResponse>(Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        echo.ShouldNotBeNull();
        echo.Path.ShouldBe(CallbackPath);
        echo.BodyLength.ShouldBe(Encoding.UTF8.GetByteCount(Body));
    }

    [Fact]
    public async Task A_tampered_body_is_rejected()
    {
        using var client = factory.CreateClient();
        var timestamp = Now;
        var signature = Sign(timestamp, Body);

        using var response = await client.SendAsync(Callback(CallbackPath, Body.Replace("SETTLED", "FAILED", StringComparison.Ordinal), timestamp, signature), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("webhook.invalid_signature");
    }

    [Fact]
    public async Task A_signature_made_with_the_wrong_secret_is_rejected()
    {
        using var client = factory.CreateClient();
        var timestamp = Now;

        using var response = await client.SendAsync(Callback(CallbackPath, Body, timestamp, Sign(timestamp, Body, "guessed-secret")), Cancel);

        (await ErrorCodeAsync(response)).ShouldBe("webhook.invalid_signature");
    }

    [Fact]
    public async Task Old_callbacks_are_rejected_as_replays()
    {
        using var client = factory.CreateClient();
        var tenMinutesAgo = Now - 600;

        using var response = await client.SendAsync(Callback(CallbackPath, Body, tenMinutesAgo, Sign(tenMinutesAgo, Body)), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("webhook.stale_timestamp");
    }

    [Fact]
    public async Task Callbacks_without_a_timestamp_are_rejected()
    {
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Callback(CallbackPath, Body, timestamp: null, signature: "sha256=abc"), Cancel);

        (await ErrorCodeAsync(response)).ShouldBe("webhook.missing_timestamp");
    }

    [Fact]
    public async Task Unknown_providers_are_rejected()
    {
        using var client = factory.CreateClient();
        var timestamp = Now;

        using var response = await client.SendAsync(Callback("/webhooks/unknownbank/callback", Body, timestamp, Sign(timestamp, Body)), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("webhook.unknown_provider");
    }
}
