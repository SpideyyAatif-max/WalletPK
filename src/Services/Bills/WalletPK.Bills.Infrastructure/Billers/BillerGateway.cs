using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WalletPK.Bills.Application.Billers;
using WalletPK.Bills.Domain;
using WalletPK.Resilience;

namespace WalletPK.Bills.Infrastructure.Billers;

/// <summary>Biller client settings, from "Providers:Billers".</summary>
public sealed class BillerOptions
{
    public const string SectionName = "Providers:Billers";

    /// <summary>Where the billers are (the mock providers service in development).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent as X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";

    public ProviderResilienceOptions Resilience { get; set; } = new();

    public bool IsValid() => BaseUrl is { IsAbsoluteUri: true } && ApiKey.Length > 0 && Resilience.IsValid();
}

/// <summary>
/// One resilience pipeline per biller (bulkhead, timeouts, retry with jitter, circuit breaker), and the
/// billers' availability (GET /api/bills/billers, metric walletpk.bills.biller.circuit_state).
/// </summary>
public sealed class BillerResilience : IBillerAvailability
{
    public const string MeterName = "WalletPK.Bills";

    public BillerResilience(BillerOptions options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        Pipelines = new ProviderPipelines(
            "biller", BillerCatalog.All.Select(biller => biller.Code), options.Resilience, loggerFactory, meterFactory,
            MeterName, "walletpk.bills.biller.circuit_state", "biller");
    }

    public ProviderPipelines Pipelines { get; }

    public IReadOnlyList<BillerAvailability> GetAll() =>
    [
        .. BillerCatalog.All.Select(biller =>
        {
            var health = Pipelines.HealthOf(biller.Code);
            return new BillerAvailability(biller.Code, biller.Name, biller.Category, biller.ConsumerNumberLength, biller.ConsumerNumberLabel, health.Status, health.Since);
        }),
    ];
}

/// <summary>Biller calls: <c>walletpk.bills.biller.calls</c> by operation, biller and outcome; <c>walletpk.bills.biller.duration</c>.</summary>
public sealed class BillerMetrics
{
    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;

    public BillerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(BillerResilience.MeterName);
        _calls = meter.CreateCounter<long>("walletpk.bills.biller.calls", description: "Calls to billers, by outcome");
        _duration = meter.CreateHistogram<double>("walletpk.bills.biller.duration", unit: "s", description: "Biller calls, retries included");
    }

    public void Record(string operation, string biller, BillerCallOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", operation }, { "biller", biller }, { "outcome", outcome.ToString() } };
        _calls.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }
}

/// <summary>Calls the billers over HTTP through each biller's pipeline; every ending becomes a <see cref="BillerCallOutcome"/>.</summary>
internal sealed partial class BillerGateway(HttpClient http, BillerResilience resilience, BillerMetrics metrics, ILogger<BillerGateway> logger) : IBillerGateway
{
    public async Task<BillInquiryResult> InquireAsync(Biller biller, string consumerNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(biller);
        var call = await CallAsync("inquiry", biller, token => http.GetAsync($"billers/{biller.Code}/bills/{Uri.EscapeDataString(consumerNumber)}", token), cancellationToken);
        return call.Outcome == BillerCallOutcome.Ok
            ? new(BillerCallOutcome.Ok, call.Body.Deserialize<BillInfo>(JsonSerializerOptions.Web), null, null)
            : new(call.Outcome, null, call.Code, call.Detail);
    }

    public async Task<BillerPaymentResult> PayAsync(Biller biller, Guid paymentId, string consumerNumber, long amountPaisa, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(biller);
        var body = new { paymentId = paymentId.ToString(), consumerNumber, amountPaisa };
        var call = await CallAsync("pay", biller, token => http.PostAsJsonAsync($"billers/{biller.Code}/payments", body, JsonSerializerOptions.Web, token), cancellationToken);
        return ToPayment(call);
    }

    public async Task<BillerPaymentResult> GetPaymentAsync(Biller biller, Guid paymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(biller);
        var call = await CallAsync("status", biller, token => http.GetAsync($"billers/{biller.Code}/payments/{paymentId}", token), cancellationToken);
        return ToPayment(call);
    }

    private static BillerPaymentResult ToPayment(Call call)
    {
        if (call.Outcome != BillerCallOutcome.Ok)
        {
            return new(call.Outcome, null, call.Code, call.Detail);
        }

        var answer = call.Body.Deserialize<PaymentAnswer>(JsonSerializerOptions.Web)!;
        return new(BillerCallOutcome.Ok, new BillerReceipt(answer.BillerReference, answer.BillingMonth, answer.AmountPaisa, answer.PaidAt), null, null);
    }

    private async Task<Call> CallAsync(string operation, Biller biller, Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await resilience.Pipelines.CallAsync(biller.Code, send, cancellationToken);
        var outcome = result.Outcome switch
        {
            ProviderOutcome.Ok => BillerCallOutcome.Ok,
            ProviderOutcome.Rejected => BillerCallOutcome.Rejected,
            ProviderOutcome.NotFound => BillerCallOutcome.NotFound,
            ProviderOutcome.Unavailable => BillerCallOutcome.Unavailable,
            _ => BillerCallOutcome.Unknown,
        };
        metrics.Record(operation, biller.Code, outcome, stopwatch.Elapsed);
        if (outcome is BillerCallOutcome.Unavailable or BillerCallOutcome.Unknown)
        {
            LogCallFailed(logger, operation, biller.Code, outcome, result.Detail);
        }

        return new(outcome, result.Body, result.Code, result.Detail);
    }

    private sealed record Call(BillerCallOutcome Outcome, JsonElement Body, string? Code, string? Detail);

    private sealed record PaymentAnswer(string PaymentId, string BillerReference, string Biller, string ConsumerNumber, string BillingMonth, long AmountPaisa, string Status, DateTimeOffset PaidAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Biller {Biller} {Operation}: {Outcome} ({Detail})")]
    private static partial void LogCallFailed(ILogger logger, string operation, string biller, BillerCallOutcome outcome, string? detail);
}
