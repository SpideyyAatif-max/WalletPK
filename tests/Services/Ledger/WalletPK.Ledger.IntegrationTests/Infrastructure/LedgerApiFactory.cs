using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace WalletPK.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Ledger service against a real PostgreSQL in a throwaway Docker container. The
/// database is migrated on startup, so the triggers and check constraints under test are the real ones.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_ledger")
        .WithUsername("walletpk")
        .WithPassword("walletpk_test")
        .Build();

    /// <summary>For tests that go around the application on purpose to prove the database's own guards.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:LedgerDb"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));
    }
}

/// <summary>All Ledger integration tests share one service and one database container.</summary>
[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "Ledger API";
}
