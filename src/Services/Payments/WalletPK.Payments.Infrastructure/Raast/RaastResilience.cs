using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using WalletPK.Resilience;
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

/// <summary>The per-bank pipeline settings (see <see cref="ProviderResilienceOptions"/>).</summary>
public sealed class RaastResilienceOptions : ProviderResilienceOptions;

/// <summary>
/// The resilience pipelines for Raast and each bank, and the banks' availability as their circuit
/// breakers see it (GET /api/payments/banks, metric walletpk.payments.bank.circuit_state).
/// </summary>
public sealed class RaastResilience : IBankAvailability
{
    /// <summary>The pipeline for calls that involve only Raast (alias lookups, status queries).</summary>
    public const string RaastKey = "raast";

    private readonly ProviderPipelines _pipelines;

    public RaastResilience(RaastOptions options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pipelines = new ProviderPipelines(
            "raast",
            RaastBanks.All.Select(bank => bank.Code).Prepend(RaastKey),
            options.Resilience,
            loggerFactory,
            meterFactory,
            RaastMetrics.MeterName,
            "walletpk.payments.bank.circuit_state",
            "bank");
    }

    public ProviderPipelines Pipelines => _pipelines;

    public CircuitState CircuitOf(string key) => _pipelines.CircuitOf(key);

    public IReadOnlyList<BankAvailability> GetAll() =>
    [
        .. RaastBanks.All.Select(bank =>
        {
            // A bank is only as available as the network in front of it.
            var health = _pipelines.HealthOf(bank.Code, via: RaastKey);
            return new BankAvailability(bank.Code, bank.Name, health.Circuit, health.Status, health.Since);
        }),
    ];
}
