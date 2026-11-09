using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Ledger.Application.Holds;
using WalletPK.Ledger.Infrastructure.Idempotency;
using WalletPK.Web.Idempotency;

namespace WalletPK.Ledger.Infrastructure.Maintenance;

/// <summary>
/// Background housekeeping, every 30 seconds by default:
/// <list type="bullet">
/// <item>the <b>hold expiry sweeper</b> returns money from holds nobody captured or released in time;</item>
/// <item>expired idempotency keys are purged.</item>
/// </list>
/// Safe with several instances running: each hold is released under its wallet lock and row version,
/// so two sweepers can never release the same hold twice.
/// </summary>
internal sealed partial class LedgerMaintenanceService(
    IServiceScopeFactory scopes,
    MaintenanceOptions options,
    ILogger<LedgerMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never let one bad run stop the sweeper; the next tick tries again.
                LogRunFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        var expired = await scope.ServiceProvider.GetRequiredService<HoldService>().ExpireDueAsync(options.HoldBatchSize, cancellationToken);
        if (expired > 0)
        {
            LogHoldsExpired(logger, expired);
        }

        if (scope.ServiceProvider.GetRequiredService<IIdempotencyStore>() is LedgerIdempotencyStore store)
        {
            await store.PurgeExpiredAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hold sweeper released {Count} expired hold(s)")]
    private static partial void LogHoldsExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ledger maintenance run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
