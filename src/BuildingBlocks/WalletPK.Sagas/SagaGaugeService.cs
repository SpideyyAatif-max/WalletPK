using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Sagas;

/// <summary>
/// Samples how many Sagas are running in each state every 30 seconds, for the gauge
/// <c>walletpk.sagas.active</c> (saga_type, state): the operations dashboard's "active Sagas by state".
/// </summary>
internal sealed partial class SagaGaugeService<TDbContext>(IServiceScopeFactory scopes, ILogger<SagaGaugeService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
                var counts = await db.Set<SagaInstance>().AsNoTracking()
                    .Where(saga => !saga.IsCompleted)
                    .GroupBy(saga => new { saga.SagaType, saga.State })
                    .Select(group => new { group.Key.SagaType, group.Key.State, Count = group.Count() })
                    .ToListAsync(stoppingToken);
                ActiveSagas.Set(counts.Select(count => (count.SagaType, count.State, count.Count)));
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogSampleFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not count running Sagas for the active-Sagas gauge")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}

/// <summary>The latest sample, read by the gauge.</summary>
internal static class ActiveSagas
{
    private static IReadOnlyList<(string Type, string State, int Count)> _latest = [];

    static ActiveSagas() =>
        SagaTelemetry.Meter.CreateObservableGauge(
            "walletpk.sagas.active",
            () => Volatile.Read(ref _latest).Select(count =>
                new Measurement<int>(count.Count, new KeyValuePair<string, object?>("saga_type", count.Type), new KeyValuePair<string, object?>("state", count.State))),
            description: "Running Sagas by type and state (sampled every 30 s)");

    public static void Set(IEnumerable<(string Type, string State, int Count)> counts) => Volatile.Write(ref _latest, [.. counts]);
}
