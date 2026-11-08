using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Identity API against real PostgreSQL and Redis, each in a throwaway Docker
/// container started for the test run (Testcontainers). The database is migrated on startup,
/// exactly as in development. SMS messages are captured so tests can read the OTP.
/// </summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_identity")
        .WithUsername("walletpk")
        .WithPassword("walletpk_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    public CapturingSmsSender Sms { get; } = new();

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

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
            ["ConnectionStrings:IdentityDb"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISmsSender>();
            services.AddSingleton<ISmsSender>(Sms);
        });
    }
}

/// <summary>All Identity integration tests share one API and one set of containers.</summary>
[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityApiFactory>
{
    public const string Name = "Identity API";
}
