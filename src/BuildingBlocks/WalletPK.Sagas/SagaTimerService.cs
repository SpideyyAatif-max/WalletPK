using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Sagas;

/// <summary>
/// Fires Saga timers in the background. Each round takes a batch of due Sagas and runs their steps in
/// parallel; a full batch means more may be due, so the next round starts at once. Several instances
/// of a service can run this loop: each takes Sagas nobody else holds.
/// </summary>
internal sealed partial class SagaTimerService<TDbContext>(
    IServiceScopeFactory scopes,
    SagaTimerOptions options,
    ILogger<SagaTimerService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var runner = new SagaTimerRunner<TDbContext>(scopes, options);
        var idle = TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds);
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var due = await runner.RunDueAsync(stoppingToken);
                failures = 0;
                wait = due >= options.BatchSize ? TimeSpan.Zero : idle;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                failures++;
                wait = TimeSpan.FromMilliseconds(Math.Min(30_000, options.PollIntervalMilliseconds * Math.Pow(2, Math.Min(failures, 10))));
                LogRoundFailed(logger, exception, wait.TotalSeconds);
            }

            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saga timer round failed; next round in {Seconds}s")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception, double seconds);
}
