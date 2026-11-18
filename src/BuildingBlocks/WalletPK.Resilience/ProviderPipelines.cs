using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace WalletPK.Resilience;

/// <summary>
/// One pipeline per provider (a bank, a biller, a mobile network), so a slow or dead one cannot use up
/// the calls, threads or retries the others need. From the outside in:
/// <list type="number">
/// <item><b>Bulkhead</b>: at most <see cref="MaxConcurrentCalls"/> calls at once, <see cref="MaxQueuedCalls"/> waiting; the rest are refused at once.</item>
/// <item><b>Total timeout</b> for the call with its retries.</item>
/// <item><b>Retry</b> on timeouts, dropped connections and 500/502/504, with exponential back-off and jitter.
/// Safe only because every provider call WalletPK makes carries its own ID (a repeat is the same request).</item>
/// <item><b>Circuit breaker</b>: when most recent calls fail (5xx, timeouts, connection errors), stop calling
/// the provider for a while and fail fast; then let a trial call through.</item>
/// <item><b>Attempt timeout</b> for each try.</item>
/// </list>
/// </summary>
public class ProviderResilienceOptions
{
    public int MaxConcurrentCalls { get; set; } = 32;

    public int MaxQueuedCalls { get; set; } = 64;

    public int TotalTimeoutMilliseconds { get; set; } = 15_000;

    public int AttemptTimeoutMilliseconds { get; set; } = 4_000;

    public int MaxRetries { get; set; } = 2;

    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>The circuit opens when at least this share of calls failed ...</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>... out of at least this many ...</summary>
    public int MinimumThroughput { get; set; } = 5;

    /// <summary>... within this window.</summary>
    public int SamplingSeconds { get; set; } = 30;

    /// <summary>How long it stays open before a trial call.</summary>
    public int BreakSeconds { get; set; } = 30;

    public bool IsValid() =>
        MaxConcurrentCalls >= 1 && MaxQueuedCalls >= 0
        && AttemptTimeoutMilliseconds >= 10 && TotalTimeoutMilliseconds >= AttemptTimeoutMilliseconds
        && MaxRetries is >= 0 and <= 10 && RetryBaseDelayMilliseconds >= 1
        && FailureRatio is > 0 and <= 1 && MinimumThroughput >= 2
        && SamplingSeconds >= 1 && BreakSeconds >= 1;
}

/// <summary>A provider's health as its circuit breaker sees it.</summary>
/// <param name="Circuit">Closed, Open or HalfOpen.</param>
/// <param name="Status">Available, Degraded (recovering: trial calls only) or Down.</param>
/// <param name="Since">When the circuit last changed state.</param>
public sealed record ProviderHealth(string Circuit, string Status, DateTimeOffset? Since);

/// <summary>
/// The resilience pipelines for a family of providers, one per key, and their health. A gauge
/// <c>{gaugeName}</c> (0 closed, 1 half-open, 2 open) is published per key under the tag <c>{tag}</c>.
/// </summary>
public sealed partial class ProviderPipelines
{
    private readonly Dictionary<string, ResiliencePipeline<HttpResponseMessage>> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CircuitBreakerStateProvider> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _changedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    /// <param name="family">For pipeline names and logs ("raast", "biller").</param>
    /// <param name="keys">One pipeline each ("HBL", "KE" ...).</param>
    /// <param name="options">The settings, shared by every key.</param>
    /// <param name="loggerFactory">Logs and Polly telemetry.</param>
    /// <param name="meterFactory">For the circuit-state gauge.</param>
    /// <param name="meterName">The service's meter ("WalletPK.Payments").</param>
    /// <param name="gaugeName">"walletpk.payments.bank.circuit_state".</param>
    /// <param name="tag">The gauge's tag for the key ("bank").</param>
    public ProviderPipelines(
        string family,
        IEnumerable<string> keys,
        ProviderResilienceOptions options,
        ILoggerFactory loggerFactory,
        IMeterFactory meterFactory,
        string meterName,
        string gaugeName,
        string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(meterFactory);
        _logger = loggerFactory.CreateLogger<ProviderPipelines>();

        foreach (var key in keys)
        {
            var circuit = new CircuitBreakerStateProvider();
            _circuits[key] = circuit;
            _pipelines[key] = Build(family, key, options, circuit, loggerFactory);
        }

        meterFactory.Create(meterName).CreateObservableGauge(
            gaugeName,
            () => _circuits.Select(pair => new Measurement<int>(StateNumber(pair.Value.CircuitState), new KeyValuePair<string, object?>(tag, pair.Key))),
            description: $"Circuit breaker per {tag}: 0 closed, 1 half-open, 2 open");
    }

    public IReadOnlyCollection<string> Keys => _pipelines.Keys;

    public ResiliencePipeline<HttpResponseMessage> For(string key) =>
        _pipelines.TryGetValue(key, out var pipeline) ? pipeline : throw new ArgumentException($"No resilience pipeline for '{key}'.", nameof(key));

    public CircuitState CircuitOf(string key) => _circuits[key].CircuitState;

    /// <summary>The health of <paramref name="key"/>; with <paramref name="via"/>, no better than the provider in front of it.</summary>
    public ProviderHealth HealthOf(string key, string? via = null)
    {
        var state = via is null ? CircuitOf(key) : Worst(CircuitOf(key), CircuitOf(via));
        var since = new[] { key, via }.Where(item => item is not null).Select(item => _changedAt.TryGetValue(item!, out var at) ? at : (DateTimeOffset?)null).Max();
        return new ProviderHealth(state.ToString(), StatusOf(state), since);
    }

    /// <summary>
    /// Calls through <paramref name="key"/>'s pipeline and says how it ended (never throws for a failed call).
    /// A call whose earlier try may have reached the provider (it timed out, the connection dropped, a 5xx)
    /// is never reported as <see cref="ProviderOutcome.Unavailable"/>, even if the circuit then opened or
    /// the last try could not connect: it is <see cref="ProviderOutcome.Unknown"/>, so the caller asks
    /// before sending it again.
    /// </summary>
    public async Task<ProviderCallResult> CallAsync(string key, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var maybeDelivered = false;

        async ValueTask<HttpResponseMessage> Attempt(CancellationToken token)
        {
            try
            {
                var response = await send(token);
                if (response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                {
                    maybeDelivered = true;
                }

                return response;
            }
            catch (HttpRequestException exception) when (exception.HttpRequestError != HttpRequestError.ConnectionError)
            {
                maybeDelivered = true;
                throw;
            }
            catch (OperationCanceledException)
            {
                // The try's own timeout (or the whole call's): it may have arrived.
                maybeDelivered = true;
                throw;
            }
        }

        var result = await ExecuteAsync(key, Attempt, cancellationToken);
        return result.Outcome == ProviderOutcome.Unavailable && maybeDelivered
            ? result with { Outcome = ProviderOutcome.Unknown, Detail = $"no clear answer, then {result.Detail}" }
            : result;
    }

    private async Task<ProviderCallResult> ExecuteAsync(string key, Func<CancellationToken, ValueTask<HttpResponseMessage>> attempt, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await For(key).ExecuteAsync(attempt, cancellationToken);
            var body = await ReadBodyAsync(response, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new(ProviderOutcome.Ok, body, null, null, (int)response.StatusCode);
            }

            var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var value) ? value.GetString() : null;
            var detail = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("detail", out var text) ? text.GetString() : null;
            var described = $"HTTP {(int)response.StatusCode}{(code is null ? "" : $" {code}")}";
            var status = (int)response.StatusCode;
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => new(ProviderOutcome.NotFound, body, code, detail ?? described, status),
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests => new(ProviderOutcome.Unavailable, body, code, described, status),
                >= HttpStatusCode.InternalServerError => new(ProviderOutcome.Unknown, body, code, described, status),
                _ => new(ProviderOutcome.Rejected, body, code, detail ?? described, status),
            };
        }
        catch (BrokenCircuitException)
        {
            return new(ProviderOutcome.Unavailable, default, null, "circuit open: the provider is failing", null);
        }
        catch (RateLimiterRejectedException)
        {
            return new(ProviderOutcome.Unavailable, default, null, "too many calls in flight", null);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConnectionError)
        {
            // Could not even connect: the request never left.
            return new(ProviderOutcome.Unavailable, default, null, "could not connect", null);
        }
        catch (HttpRequestException exception)
        {
            return new(ProviderOutcome.Unknown, default, null, $"connection lost ({exception.HttpRequestError})", null);
        }
        catch (TimeoutRejectedException)
        {
            return new(ProviderOutcome.Unknown, default, null, "timed out", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ProviderOutcome.Unknown, default, null, "timed out", null);
        }
    }

    private ResiliencePipeline<HttpResponseMessage> Build(string family, string key, ProviderResilienceOptions settings, CircuitBreakerStateProvider circuit, ILoggerFactory loggerFactory)
    {
        // Failures that say nothing about the request itself: worth retrying, and counted by the breaker.
        var transient = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);

        // A 503 is the provider saying it is down: no point retrying now, but the breaker counts it.
        var failure = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => (int)response.StatusCode >= 500);

        return new ResiliencePipelineBuilder<HttpResponseMessage> { Name = $"{family}-{key.ToLowerInvariant()}" }
            .AddConcurrencyLimiter(settings.MaxConcurrentCalls, settings.MaxQueuedCalls)
            .AddTimeout(TimeSpan.FromMilliseconds(settings.TotalTimeoutMilliseconds))
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = transient,
                MaxRetryAttempts = settings.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(settings.RetryBaseDelayMilliseconds),
                OnRetry = arguments =>
                {
                    arguments.Outcome.Result?.Dispose();
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = failure,
                FailureRatio = settings.FailureRatio,
                MinimumThroughput = settings.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(settings.SamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(settings.BreakSeconds),
                StateProvider = circuit,
                OnOpened = arguments =>
                {
                    _changedAt[key] = DateTimeOffset.UtcNow;
                    LogOpened(_logger, family, key, arguments.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    _changedAt[key] = DateTimeOffset.UtcNow;
                    LogHalfOpened(_logger, family, key);
                    return default;
                },
                OnClosed = _ =>
                {
                    _changedAt[key] = DateTimeOffset.UtcNow;
                    LogClosed(_logger, family, key);
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMilliseconds))
            .ConfigureTelemetry(loggerFactory)
            .Build();
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static CircuitState Worst(CircuitState first, CircuitState second) =>
        StateNumber(first) >= StateNumber(second) ? first : second;

    private static int StateNumber(CircuitState state) => state switch
    {
        CircuitState.Closed => 0,
        CircuitState.HalfOpen => 1,
        _ => 2,
    };

    private static string StatusOf(CircuitState state) => state switch
    {
        CircuitState.Closed => "Available",
        CircuitState.HalfOpen => "Degraded",
        _ => "Down",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit for {Family} {Key} opened: calls fail fast for {Seconds}s")]
    private static partial void LogOpened(ILogger logger, string family, string key, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit for {Family} {Key} is half-open: trying a call")]
    private static partial void LogHalfOpened(ILogger logger, string family, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit for {Family} {Key} closed: it is back")]
    private static partial void LogClosed(ILogger logger, string family, string key);
}

/// <summary>How a call to a provider ended.</summary>
public enum ProviderOutcome
{
    /// <summary>It answered (2xx).</summary>
    Ok,

    /// <summary>It refused the request (4xx): sending it again will not help.</summary>
    Rejected,

    /// <summary>It does not know it (404): for a status query, the request never arrived.</summary>
    NotFound,

    /// <summary>Certainly not done: down (503), circuit open, bulkhead full, could not connect. Safe to send again later.</summary>
    Unavailable,

    /// <summary>No answer (timeout, dropped connection, 5xx): it may or may not have been done. Ask before sending again.</summary>
    Unknown,
}

/// <param name="Outcome">How it ended.</param>
/// <param name="Body">The JSON answer (default when there was none).</param>
/// <param name="Code">The error code in a problem answer.</param>
/// <param name="Detail">What went wrong, in words.</param>
/// <param name="HttpStatus">The HTTP status, when there was an answer.</param>
public sealed record ProviderCallResult(ProviderOutcome Outcome, JsonElement Body, string? Code, string? Detail, int? HttpStatus);
