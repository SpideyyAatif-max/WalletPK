using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Contracts.Fraud;
using WalletPK.Fraud.Application;

namespace WalletPK.Fraud.Infrastructure.Persistence;

/// <summary>
/// Warms the alert path at startup, so the first refused payment after a deploy is not the one that
/// pays for EF Core's query and update compilation (hundreds of milliseconds, past the Ledger's deadline):
/// reads Redis and runs the rules, runs the queries an alert needs, then saves a throwaway alert, case, flag
/// and outbox message in a transaction it rolls back.
/// </summary>
internal sealed partial class FraudWarmup(IServiceScopeFactory scopes, ILogger<FraudWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IFraudStore>();
            var features = scope.ServiceProvider.GetRequiredService<IFeatureStore>();
            var scorer = scope.ServiceProvider.GetRequiredService<RiskScorer>();
            var nobody = Guid.Empty;
            var now = DateTimeOffset.UtcNow;

            // The scoring path: Redis reads and the rules, without recording anything.
            var input = new ScoreInput(Guid.Empty, nobody, null, "P2PTransfer", 1, null);
            await features.CachedAsync(input.TransactionId, stoppingToken);
            scorer.Explain(FeatureBuilder.Build(input, await features.ReadAsync(input, now, stoppingToken), now));

            await store.OpenCaseAsync(nobody, "Payment", stoppingToken);
            await store.FlagAsync(nobody, stoppingToken);
            await store.AlertsAsync(nobody, null, null, 1, stoppingToken);
            await store.CasesAsync("Open", 1, stoppingToken);

            await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
            var fraudCase = new FraudCase { Id = Guid.CreateVersion7(), Number = -1, UserId = nobody, Subjects = [nobody], Title = "warm-up", CreatedAt = now, UpdatedAt = now };
            store.Add(fraudCase);
            store.Add(new FraudAlert { Id = Guid.CreateVersion7(), UserId = nobody, Summary = "warm-up", CaseId = fraudCase.Id, CreatedAt = now });
            store.Add(new AccountFlag { UserId = nobody, Reason = "warm-up", ChangedBy = "warm-up", ChangedAt = now, RequestId = Guid.CreateVersion7() });
            store.Publish(new FraudAlertRaised(Guid.Empty, nobody, null, "Payment", "Low", 0, "Allow", [], "warm-up", null, now), null);

            // The audit chain's read (no entry: a rolled-back insert would leave a gap in its sequence).
            await db.Set<AuditEntry>().AsNoTracking().OrderByDescending(entry => entry.Sequence).Select(entry => entry.Hash).FirstOrDefaultAsync(stoppingToken);
            await db.SaveChangesAsync(stoppingToken);
            await transaction.RollbackAsync(stoppingToken);
            db.ChangeTracker.Clear();

            LogWarm(logger, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fraud alert path warmed up in {Milliseconds} ms")]
    private static partial void LogWarm(ILogger logger, long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fraud warm-up failed; the first alert may be slow")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
