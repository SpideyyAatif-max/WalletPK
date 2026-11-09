using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Ledger.Infrastructure.Audit;

/// <summary>Runs the <see cref="AuditSealer"/> every couple of seconds, so the chain trails new rows only briefly.</summary>
internal sealed partial class AuditSealerService(
    IServiceScopeFactory scopes,
    AuditOptions options,
    ILogger<AuditSealerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SealingEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.SealIntervalMilliseconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AuditSealer>().SealAllAsync(options.BatchSize, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSealFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit sealing failed; will retry")]
    private static partial void LogSealFailed(ILogger logger, Exception exception);
}
