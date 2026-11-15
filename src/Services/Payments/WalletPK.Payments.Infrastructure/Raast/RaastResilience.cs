using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using WalletPK.Payments.Application.Raast;
using WalletPK.Payments.Domain;

namespace WalletPK.Payments.Infrastructure.Raast;

/// <summary>Raast client settings, from "Providers:Raast".</summary>
public sealed class RaastOptions
{
    public const string SectionName = "Providers:Raast";

    /// <summary>Where Raast is (the mock providers service in development).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent as X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";

    public RaastResilienceOptions Resilience { get; set; } = new();

    public bool IsValid() => BaseUrl is { IsAbsoluteUri: true } && ApiKey.Length > 0 && Resilience.IsValid();
}

/// <summary>
/// One pipeline per bank (and one for Raast itself), so a slow or dead bank cannot use up the calls,
/// threads or retries the others need. From the outside in:
/// <list type="number">
/// <item><b>Bulkhead</b>: at most <see cref="MaxConcurrentCalls"/> calls to the bank at once, <see cref="MaxQueuedCalls"/> waiting; the rest are refused at once.</item>
/// <item><b>Total timeout</b> for the call with its retries.</item>
/// <item><b>Retry</b> on timeouts, dropped connections and 500/502/504, with exponential back-off and jitter.
/// Safe for sends too: Raast treats a repeated transfer ID as the same transfer.</item>
/// <item><b>Circuit breaker</b>: when most recent calls fail (5xx, timeouts, connection errors), stop calling
/// the bank for a while and fail fast; then let one trial call through.</item>
/// <item><b>Attempt timeout</b> for each try.</item>
/// </list>
/// </summary>
public sealed class RaastResilienceOptions
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

/// <summary>
/// The resilience pipelines for Raast and each bank, and the banks' availability as their circuit
/// breakers see it (GET /api/payments/banks, metric walletpk.payments.bank.circuit_state).
/// </summary>
public sealed partial class RaastResilience : IBankAvailability
{
    /// <summary>The pipeline for calls that involve only Raast (alias lookups, status queries).</summary>
    public const string RaastKey = "raast";

    private readonly Dictionary<string, ResiliencePipeline<HttpResponseMessage>> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CircuitBreakerStateProvider> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _changedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<RaastResilience> _logger;

    public RaastResilience(RaastOptions options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(meterFactory);
        _logger = loggerFactory.CreateLogger<RaastResilience>();

        foreach (var key in RaastBanks.All.Select(bank => bank.Code).Prepend(RaastKey))
        {
            var circuit = new CircuitBreakerStateProvider();
            _circuits[key] = circuit;
            _pipelines[key] = Build(key, options.Resilience, circuit, loggerFactory);
        }

        var meter = meterFactory.Create(RaastMetrics.MeterName);
        meter.CreateObservableGauge(
            "walletpk.payments.bank.circuit_state",
            () => _circuits.Select(pair => new Measurement<int>(StateNumber(pair.Value.CircuitState), new KeyValuePair<string, object?>("bank", pair.Key))),
            description: "Circuit breaker per bank: 0 closed, 1 half-open, 2 open");
    }

    /// <summary>The pipeline for a bank's code, or <see cref="RaastKey"/>.</summary>
    public ResiliencePipeline<HttpResponseMessage> For(string key) =>
        _pipelines.TryGetValue(key, out var pipeline) ? pipeline : throw new ArgumentException($"No resilience pipeline for '{key}'.", nameof(key));

    public CircuitState CircuitOf(string key) => _circuits[key].CircuitState;

    public IReadOnlyList<BankAvailability> GetAll() =>
    [
        .. RaastBanks.All.Select(bank =>
        {
            // A bank is only as available as the network in front of it.
            var state = Worst(_circuits[bank.Code].CircuitState, _circuits[RaastKey].CircuitState);
            var since = new[] { bank.Code, RaastKey }.Select(key => _changedAt.TryGetValue(key, out var at) ? at : (DateTimeOffset?)null).Max();
            return new BankAvailability(bank.Code, bank.Name, state.ToString(), StatusOf(state), since);
        }),
    ];

    private ResiliencePipeline<HttpResponseMessage> Build(string key, RaastResilienceOptions settings, CircuitBreakerStateProvider circuit, ILoggerFactory loggerFactory)
    {
        // Failures that say nothing about the request itself: worth retrying, and counted by the breaker.
        var transient = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);

        // A 503 is the bank saying it is down: no point retrying now, but the breaker counts it.
        var failure = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => (int)response.StatusCode >= 500);

        return new ResiliencePipelineBuilder<HttpResponseMessage> { Name = $"raast-{key.ToLowerInvariant()}" }
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
                    LogOpened(_logger, key, arguments.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    _changedAt[key] = DateTimeOffset.UtcNow;
                    LogHalfOpened(_logger, key);
                    return default;
                },
                OnClosed = _ =>
                {
                    _changedAt[key] = DateTimeOffset.UtcNow;
                    LogClosed(_logger, key);
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMilliseconds))
            .ConfigureTelemetry(loggerFactory)
            .Build();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit for {Bank} opened: calls fail fast for {Seconds}s; transfers to it are queued")]
    private static partial void LogOpened(ILogger logger, string bank, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit for {Bank} is half-open: trying one call")]
    private static partial void LogHalfOpened(ILogger logger, string bank);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit for {Bank} closed: the bank is back")]
    private static partial void LogClosed(ILogger logger, string bank);
}
