using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WalletPK.Web.Idempotency;

namespace WalletPK.Web.UnitTests.Idempotency;

public sealed class IdempotencyMiddlewareTests : IAsyncDisposable
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private readonly InMemoryIdempotencyStore _store = new();
    private int _paymentsExecuted;
    private IHost? _host;

    private async Task<HttpClient> ClientAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWalletPkWeb();
                    services.AddSingleton<IIdempotencyStore>(_store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseWalletPkIdempotency();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/pay", (PayRequest request) =>
                        {
                            var number = Interlocked.Increment(ref _paymentsExecuted);
                            return TypedResults.Created($"/payments/{number}", new { paymentNumber = number, request.Amount });
                        }).RequireIdempotencyKey();

                        endpoints.MapPost("/boom", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).RequireIdempotencyKey();
                        endpoints.MapPost("/plain", () => TypedResults.Ok(Interlocked.Increment(ref _paymentsExecuted)));
                    });
                }))
            .StartAsync(Cancel);

        return _host.GetTestClient();
    }

    private static HttpRequestMessage Pay(string? key, int amount = 500)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/pay", UriKind.Relative)) { Content = JsonContent.Create(new { amount }) };
        if (key is not null)
        {
            request.Headers.Add(IdempotencyMiddleware.HeaderName, key);
        }

        return request;
    }

    [Fact]
    public async Task A_retry_with_the_same_key_gets_the_first_response_and_pays_once()
    {
        using var client = await ClientAsync();

        using var firstRequest = Pay("retry-key-0001");
        using var first = await client.SendAsync(firstRequest, Cancel);
        using var retryRequest = Pay("retry-key-0001");
        using var retry = await client.SendAsync(retryRequest, Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync(Cancel)).ShouldBe(await first.Content.ReadAsStringAsync(Cancel));
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        retry.Headers.GetValues(IdempotencyMiddleware.ReplayedHeaderName).ShouldBe(["true"]);
        first.Headers.Contains(IdempotencyMiddleware.ReplayedHeaderName).ShouldBeFalse();
        _paymentsExecuted.ShouldBe(1);
    }

    [Fact]
    public async Task Different_keys_are_different_payments()
    {
        using var client = await ClientAsync();

        using var a = Pay("payment-key-a1");
        using var b = Pay("payment-key-b2");
        (await client.SendAsync(a, Cancel)).Dispose();
        (await client.SendAsync(b, Cancel)).Dispose();

        _paymentsExecuted.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in it!")]
    public async Task A_missing_or_malformed_key_is_rejected(string? key)
    {
        using var client = await ClientAsync();

        using var request = Pay(key);
        using var response = await client.SendAsync(request, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Cancel)).GetProperty("code").GetString().ShouldBe("idempotency.key_required");
        _paymentsExecuted.ShouldBe(0);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_refused()
    {
        using var client = await ClientAsync();

        using var first = Pay("reused-key-0001", amount: 500);
        using var different = Pay("reused-key-0001", amount: 900);
        (await client.SendAsync(first, Cancel)).Dispose();
        using var response = await client.SendAsync(different, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        _paymentsExecuted.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_still_in_progress_answers_409()
    {
        using var client = await ClientAsync();
        _store.ForceInProgress = true;

        using var request = Pay("in-flight-key-1");
        using var response = await client.SendAsync(request, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _paymentsExecuted.ShouldBe(0);
    }

    [Fact]
    public async Task Server_errors_are_not_remembered_so_the_client_can_retry_for_real()
    {
        using var client = await ClientAsync();

        using var first = new HttpRequestMessage(HttpMethod.Post, new Uri("/boom", UriKind.Relative));
        first.Headers.Add(IdempotencyMiddleware.HeaderName, "boom-key-00001");
        (await client.SendAsync(first, Cancel)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        _store.Abandoned.ShouldContain(key => key.EndsWith(":boom-key-00001", StringComparison.Ordinal));
        _store.Completed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Endpoints_without_the_marker_are_not_affected()
    {
        using var client = await ClientAsync();

        (await client.PostAsync(new Uri("/plain", UriKind.Relative), null, Cancel)).Dispose();
        (await client.PostAsync(new Uri("/plain", UriKind.Relative), null, Cancel)).Dispose();

        _paymentsExecuted.ShouldBe(2);
        _store.Completed.ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed record PayRequest(int Amount);

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, (string Hash, StoredResponse? Response)> _keys = new();

        public bool ForceInProgress { get; set; }

        public ConcurrentBag<string> Abandoned { get; } = [];

        public ConcurrentBag<string> Completed { get; } = [];

        public Task<IdempotencyBegin> TryBeginAsync(string key, string requestHash, CancellationToken cancellationToken)
        {
            if (ForceInProgress)
            {
                return Task.FromResult(new IdempotencyBegin(IdempotencyStatus.InProgress));
            }

            if (_keys.TryAdd(key, (requestHash, null)))
            {
                return Task.FromResult(new IdempotencyBegin(IdempotencyStatus.Started));
            }

            var existing = _keys[key];
            return Task.FromResult(existing.Hash != requestHash
                ? new IdempotencyBegin(IdempotencyStatus.Mismatch)
                : existing.Response is null
                    ? new IdempotencyBegin(IdempotencyStatus.InProgress)
                    : new IdempotencyBegin(IdempotencyStatus.Completed, existing.Response));
        }

        public Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
        {
            _keys[key] = (_keys[key].Hash, response);
            Completed.Add(key);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(string key, CancellationToken cancellationToken)
        {
            _keys.TryRemove(key, out _);
            Abandoned.Add(key);
            return Task.CompletedTask;
        }
    }
}
