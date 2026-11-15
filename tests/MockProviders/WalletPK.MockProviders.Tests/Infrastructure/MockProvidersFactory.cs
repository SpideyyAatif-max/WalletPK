using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using WalletPK.MockProviders.Callbacks;

namespace WalletPK.MockProviders.Tests.Infrastructure;

/// <summary>
/// The real mock-providers service on a throwaway PostgreSQL (Testcontainers). Callbacks go to an
/// in-memory sink instead of the network, so tests can read them and make deliveries fail.
/// </summary>
public sealed class MockProvidersFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ClientKey = "test-client-key-0123456789";
    public const string AdminKey = "test-admin-key-0123456789";
    public const string RaastSecret = "test-raast-webhook-secret";
    public const string CallbackUrl = "http://walletpk.test/webhooks/raast/transfer-status";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_mockproviders")
        .WithUsername("walletpk")
        .WithPassword("walletpk_test")
        .Build();

    public CallbackSink Callbacks { get; } = new();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A WalletPK service calling the providers.</summary>
    public HttpClient Client()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ClientKey);
        return client;
    }

    /// <summary>The control API (tests, later the Simulation service).</summary>
    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return client;
    }

    public string ConnectionString => _postgres.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MockProvidersDb"] = _postgres.GetConnectionString(),
            ["MockProviders:ClientApiKeys:0"] = ClientKey,
            ["MockProviders:AdminApiKey"] = AdminKey,
            ["MockProviders:Raast:SettleDelayMinMilliseconds"] = "50",
            ["MockProviders:Raast:SettleDelayMaxMilliseconds"] = "150",
            ["MockProviders:Raast:CallbackUrl"] = CallbackUrl,
            ["MockProviders:Raast:CallbackSecret"] = RaastSecret,
            ["MockProviders:Callbacks:PollMilliseconds"] = "20",
            ["MockProviders:Callbacks:MaxAttempts"] = "3",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
            services.AddHttpClient(CallbackSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Callbacks)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }
}

/// <summary>One callback as WalletPK's webhook would receive it.</summary>
public sealed record ReceivedCallback(Uri Url, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>Stands in for WalletPK's webhook endpoint.</summary>
public sealed class CallbackSink : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);

    public ConcurrentQueue<ReceivedCallback> Received { get; } = new();

    /// <summary>The next <paramref name="count"/> deliveries whose body contains <paramref name="match"/> get HTTP 500.</summary>
    public void FailNext(string match, int count) => _failures[match] = count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        Received.Enqueue(new ReceivedCallback(request.RequestUri!, body, headers));

        foreach (var (match, remaining) in _failures)
        {
            if (remaining > 0 && body.Contains(match, StringComparison.Ordinal) && _failures.TryUpdate(match, remaining - 1, remaining))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    // The sink lives for the whole test run; HttpClientFactory must not dispose it.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>All tests share one service and database, one at a time (faults are global).</summary>
[CollectionDefinition(Name)]
public sealed class MockProvidersCollection : ICollectionFixture<MockProvidersFactory>
{
    public const string Name = "Mock providers";
}

public static class TestKit
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(expected, path);
        return await response.JsonAsync();
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Cancel);
    }

    public static async Task SetFaultsAsync(this MockProvidersFactory factory, string provider, object settings)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var admin = factory.Admin();
        using var response = await admin.PutAsJsonAsync(new Uri($"/admin/faults/{provider}", UriKind.Relative), settings, Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancel));
    }

    public static async Task ClearFaultsAsync(this MockProvidersFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var admin = factory.Admin();
        using var response = await admin.DeleteAsync(new Uri("/admin/faults", UriKind.Relative), Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(25, Cancel);
        }
    }

    public static string NewId() => Guid.CreateVersion7().ToString();

    /// <summary>A Pakistani business date string for "now".</summary>
    public static string TodayInPakistan() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
