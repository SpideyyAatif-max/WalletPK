using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WalletPK.Gateway.IntegrationTests.Infrastructure;

/// <summary>
/// A stand-in for every downstream service: a real HTTP server on a random local port that answers
/// any request with what it received (path, method, headers, body size), so tests can see exactly
/// what the gateway forwarded.
/// </summary>
internal sealed class EchoBackend : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EchoBackend(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<EchoBackend> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapFallback(async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);

            await context.Response.WriteAsJsonAsync(new EchoResponse(
                context.Request.Path.Value ?? string.Empty,
                context.Request.Method,
                context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body.Length));
        });

        await app.StartAsync();
        return new EchoBackend(app, app.Urls.First());
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>What the echo backend received.</summary>
public sealed record EchoResponse(string Path, string Method, Dictionary<string, string> Headers, long BodyLength)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
