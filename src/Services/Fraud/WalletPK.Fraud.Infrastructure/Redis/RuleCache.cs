using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Domain;
using WalletPK.Fraud.Infrastructure.Persistence;

namespace WalletPK.Fraud.Infrastructure.Redis;

/// <summary>
/// The rules in force, compiled and held in memory (M10.2), so scoring never reads the database. A change
/// saved by any instance is announced on a Redis channel and every instance reloads at once; they also
/// reload every few seconds in case a notice was missed. A rule that no longer parses is skipped and logged.
/// </summary>
internal sealed partial class RuleCache(IServiceScopeFactory scopes, IConnectionMultiplexer redis, ILogger<RuleCache> logger) : IRuleCache
{
    public static readonly RedisChannel Channel = RedisChannel.Literal("walletpk:fraud:rules-changed");

    private readonly SemaphoreSlim _reload = new(1, 1);
    private volatile IReadOnlyList<RiskRule> _rules = [];

    public IReadOnlyList<RiskRule> Rules => _rules;

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reload.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
            var records = await db.Set<RuleRecord>().AsNoTracking().Where(rule => rule.Enabled).OrderBy(rule => rule.Id).ToListAsync(cancellationToken);
            var rules = new List<RiskRule>(records.Count);
            foreach (var record in records)
            {
                var (rule, errors) = RiskRule.Parse(record.Id, record.Name, record.Definition);
                if (rule is null)
                {
                    LogBadRule(logger, record.Id, string.Join(" ", errors));
                    continue;
                }

                rules.Add(rule);
            }

            if (rules.Count != _rules.Count || !rules.Select(rule => rule.Id).SequenceEqual(_rules.Select(rule => rule.Id)))
            {
                LogLoaded(logger, rules.Count, string.Join(", ", rules.Select(rule => rule.Id)));
            }

            _rules = rules;
        }
        finally
        {
            _reload.Release();
        }
    }

    public async Task AnnounceChangeAsync()
    {
        await ReloadAsync(CancellationToken.None);
        await redis.GetSubscriber().PublishAsync(Channel, "changed");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Risk rules in force: {Count} ({Rules})")]
    private static partial void LogLoaded(ILogger logger, int count, string rules);

    [LoggerMessage(Level = LogLevel.Error, Message = "Risk rule {RuleId} does not parse and is skipped: {Errors}")]
    private static partial void LogBadRule(ILogger logger, string ruleId, string errors);
}

/// <summary>
/// Starts the rules: seeds the built-in rules that were never stored (an edited or switched-off built-in
/// rule is left alone), loads them, then reloads on every change notice and every few seconds.
/// </summary>
internal sealed partial class RuleReloader(
    IServiceScopeFactory scopes,
    IRuleCache cache,
    IConnectionMultiplexer redis,
    FraudOptions options,
    TimeProvider timeProvider,
    ILogger<RuleReloader> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await SeedAsync(cancellationToken);
        await cache.ReloadAsync(cancellationToken);
        await redis.GetSubscriber().SubscribeAsync(RuleCache.Channel, (_, _) => _ = ReloadQuietlyAsync());
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.RulesPollSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ReloadQuietlyAsync();
        }
    }

    private async Task ReloadQuietlyAsync()
    {
        try
        {
            await cache.ReloadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException or TimeoutException)
        {
            LogReloadFailed(logger, exception);
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
        var existing = await db.Set<RuleRecord>().Select(rule => rule.Id).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var seed in BuiltInRules.All.Where(seed => !existing.Contains(seed.Id)))
        {
            db.Add(new RuleRecord { Id = seed.Id, Name = seed.Name, Description = seed.Description, Definition = seed.Definition, BuiltIn = true, UpdatedAt = now, UpdatedBy = "walletpk" });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another instance seeded them at the same moment.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reloading the risk rules failed; the rules in memory stay in force")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);
}
