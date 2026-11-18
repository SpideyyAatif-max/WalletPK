using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WalletPK.Resilience;
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.TopupMerchant.Domain;

namespace WalletPK.TopupMerchant.Infrastructure.Telcos;

/// <summary>Mobile network client settings, from "Providers:Telcos".</summary>
public sealed class TelcoOptions
{
    public const string SectionName = "Providers:Telcos";

    /// <summary>Where the networks are (the mock providers service in development).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent as X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";

    public ProviderResilienceOptions Resilience { get; set; } = new();

    public bool IsValid() => BaseUrl is { IsAbsoluteUri: true } && ApiKey.Length > 0 && Resilience.IsValid();
}

/// <summary>
/// One resilience pipeline per network (bulkhead, timeouts, retry with jitter, circuit breaker), and the
/// networks' availability (GET /api/topups/networks, metric walletpk.topup.telco.circuit_state).
/// </summary>
public sealed class TelcoResilience : INetworkAvailability
{
    public const string MeterName = "WalletPK.TopupMerchant";

    public TelcoResilience(TelcoOptions options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        Pipelines = new ProviderPipelines(
            "telco", Networks.All.Select(network => network.Code), options.Resilience, loggerFactory, meterFactory,
            MeterName, "walletpk.topup.telco.circuit_state", "telco");
    }

    public ProviderPipelines Pipelines { get; }

    public IReadOnlyList<NetworkAvailability> GetAll() =>
    [
        .. Networks.All.Select(network =>
        {
            var health = Pipelines.HealthOf(network.Code);
            return new NetworkAvailability(network.Code, network.Name, network.Prefixes, health.Status, health.Since);
        }),
    ];
}

/// <summary>Network calls: <c>walletpk.topup.telco.calls</c> by operation, network and outcome; <c>walletpk.topup.telco.duration</c>.</summary>
public sealed class TelcoMetrics
{
    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;

    public TelcoMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(TelcoResilience.MeterName);
        _calls = meter.CreateCounter<long>("walletpk.topup.telco.calls", description: "Calls to mobile networks, by outcome");
        _duration = meter.CreateHistogram<double>("walletpk.topup.telco.duration", unit: "s", description: "Mobile network calls, retries included");
    }

    public void Record(string operation, string network, TelcoCallOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", operation }, { "telco", network }, { "outcome", outcome.ToString() } };
        _calls.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }
}

/// <summary>Calls the networks over HTTP through each network's pipeline; every ending becomes a <see cref="TelcoCallOutcome"/>.</summary>
internal sealed partial class TelcoGateway(HttpClient http, TelcoResilience resilience, TelcoMetrics metrics, ILogger<TelcoGateway> logger) : ITelcoGateway
{
    public async Task<(TelcoCallOutcome Outcome, IReadOnlyList<Bundle> Bundles, string? Detail)> BundlesAsync(Network network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);
        var call = await CallAsync("bundles", network, token => http.GetAsync($"telcos/{network.Code}/bundles", token), cancellationToken);
        return call.Outcome == TelcoCallOutcome.Ok
            ? (TelcoCallOutcome.Ok, call.Body.Deserialize<List<Bundle>>(JsonSerializerOptions.Web) ?? [], null)
            : (call.Outcome, [], call.Detail);
    }

    public async Task<TopUpCallResult> TopUpAsync(Network network, Guid topUpId, string msisdn, long amountPaisa, string? bundleCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);
        var body = new { topUpId = topUpId.ToString(), msisdn, amountPaisa, bundleCode };
        var call = await CallAsync("topup", network, token => http.PostAsJsonAsync($"telcos/{network.Code}/topups", body, JsonSerializerOptions.Web, token), cancellationToken);
        return ToTopUp(call);
    }

    public async Task<TopUpCallResult> GetTopUpAsync(Network network, Guid topUpId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);
        var call = await CallAsync("status", network, token => http.GetAsync($"telcos/{network.Code}/topups/{topUpId}", token), cancellationToken);
        return ToTopUp(call);
    }

    private static TopUpCallResult ToTopUp(Call call)
    {
        if (call.Outcome != TelcoCallOutcome.Ok)
        {
            return new(call.Outcome, null, call.Code, call.Detail);
        }

        var answer = call.Body.Deserialize<TopUpAnswer>(JsonSerializerOptions.Web)!;
        return new(TelcoCallOutcome.Ok, new TelcoReceipt(answer.TelcoReference, answer.AmountPaisa, answer.BundleCode, answer.CompletedAt), null, null);
    }

    private async Task<Call> CallAsync(string operation, Network network, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await resilience.Pipelines.CallAsync(network.Code, send, cancellationToken);
        var outcome = result.Outcome switch
        {
            ProviderOutcome.Ok => TelcoCallOutcome.Ok,
            ProviderOutcome.Rejected => TelcoCallOutcome.Rejected,
            ProviderOutcome.NotFound => TelcoCallOutcome.NotFound,
            ProviderOutcome.Unavailable => TelcoCallOutcome.Unavailable,
            _ => TelcoCallOutcome.Unknown,
        };
        metrics.Record(operation, network.Code, outcome, stopwatch.Elapsed);
        if (outcome is TelcoCallOutcome.Unavailable or TelcoCallOutcome.Unknown)
        {
            LogCallFailed(logger, operation, network.Code, outcome, result.Detail);
        }

        return new(outcome, result.Body, result.Code, result.Detail);
    }

    private sealed record Call(TelcoCallOutcome Outcome, JsonElement Body, string? Code, string? Detail);

    private sealed record TopUpAnswer(string TopUpId, string TelcoReference, string Telco, string Msisdn, long AmountPaisa, string? BundleCode, string Status, DateTimeOffset CompletedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network {Telco} {Operation}: {Outcome} ({Detail})")]
    private static partial void LogCallFailed(ILogger logger, string operation, string telco, TelcoCallOutcome outcome, string? detail);
}
