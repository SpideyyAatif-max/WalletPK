using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace WalletPK.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Ledger service against real PostgreSQL and Redis in throwaway Docker containers. The
/// database is migrated on startup, so the triggers and check constraints under test are the real ones.
/// The clock can be moved forward (hold expiry), and the background sweeper is off so tests decide when it runs.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_ledger")
        .WithUsername("walletpk")
        .WithPassword("walletpk_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    /// <summary>The service's clock: starts at the real time and only moves when a test advances it.</summary>
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>For tests that go around the application on purpose to prove the database's own guards.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:LedgerDb"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Ledger:Maintenance:Enabled"] = "false",
            ["Ledger:Locks:WaitMilliseconds"] = "15000",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>A clock tests can move forward; thread-safe because the race tests post in parallel.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>All Ledger integration tests share one service and one database container.</summary>
[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "Ledger API";
}
