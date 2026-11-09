using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Ledger.Application.Invariants;

namespace WalletPK.Ledger.Infrastructure.Invariants;

/// <summary>
/// Ledger health metrics (meter "WalletPK.Ledger"), exported to Prometheus with the rest:
/// <list type="bullet">
/// <item><c>walletpk.ledger.invariant.runs</c>: checker runs, tagged result=healthy|violated</item>
/// <item><c>walletpk.ledger.invariant.failed_checks</c>: failed checks in the latest run (alert when &gt; 0)</item>
/// </list>
/// </summary>
public sealed class LedgerMetrics
{
    public const string MeterName = "WalletPK.Ledger";

    private readonly Counter<long> _runs;
    private int _failedChecks;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _runs = meter.CreateCounter<long>("walletpk.ledger.invariant.runs", description: "Invariant checker runs");
        meter.CreateObservableGauge(
            "walletpk.ledger.invariant.failed_checks",
            () => Volatile.Read(ref _failedChecks),
            description: "Invariant checks failing in the latest run");
    }

    public void Record(InvariantReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Volatile.Write(ref _failedChecks, report.Failures.Count());
        _runs.Add(1, new KeyValuePair<string, object?>("result", report.Healthy ? "healthy" : "violated"));
    }
}

/// <summary>Runs the invariant checker every 15 minutes (configurable) and raises a critical log on any failure.</summary>
internal sealed partial class InvariantMonitorService(
    IServiceScopeFactory scopes,
    InvariantOptions options,
    LedgerMetrics metrics,
    ILogger<InvariantMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var report = await scope.ServiceProvider.GetRequiredService<ILedgerInvariantChecker>().CheckAsync(stoppingToken);
                metrics.Record(report);

                if (report.Healthy)
                {
                    LogHealthy(logger, report.Checks.Count, report.DurationMilliseconds);
                }
                else
                {
                    foreach (var failure in report.Failures)
                    {
                        LogViolation(logger, failure.Name, failure.Violations, failure.Detail);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRunFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ledger invariants: all {Count} checks passed in {DurationMs} ms")]
    private static partial void LogHealthy(ILogger logger, int count, long durationMs);

    [LoggerMessage(Level = LogLevel.Critical, Message = "LEDGER INVARIANT VIOLATED: {Check} ({Violations}): {Detail}")]
    private static partial void LogViolation(ILogger logger, string check, long violations, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ledger invariant check could not run")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
