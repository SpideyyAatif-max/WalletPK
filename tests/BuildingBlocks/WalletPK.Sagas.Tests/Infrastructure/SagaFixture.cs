using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Sagas.Tests.Infrastructure;

/// <summary>A service database with an outbox, an inbox and the Saga tables.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.AddSagas();
    }
}

/// <summary>PostgreSQL in a container and a service's DI container around it.</summary>
public sealed class SagaFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeBank Bank { get; } = new();

    public ServiceProvider Services { get; private set; } = null!;

    public const int LeaseSeconds = 60;

    public const int RetryDelaySeconds = 10;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sagas:Timers:Enabled"] = "false",
                ["Sagas:Timers:BatchSize"] = "10",
                ["Sagas:Timers:MaxParallelism"] = "4",
                ["Sagas:Timers:LeaseSeconds"] = LeaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Sagas:Timers:StepTimeoutSeconds"] = "20",
                ["Sagas:Timers:RetryDelaySeconds"] = RetryDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(new JsonEventSerializer(new IntegrationEventRegistry().Register<HoldMoney>().Register<TransferSettled>()));
        services.AddScoped(_ => NewDbContext());
        services.AddSingleton(Bank);
        services.AddSagas<TestDbContext>();
        services.AddSaga<TestTransferSaga>();
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using var db = NewDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public TestDbContext NewDbContext() => new(new DbContextOptionsBuilder<TestDbContext>()
        .UseNpgsql(_postgres.GetConnectionString())
        .UseSnakeCaseNamingConvention()
        .Options);

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        Bank.Reset();
        await using var db = NewDbContext();
        await db.Set<SagaStep>().ExecuteDeleteAsync(cancellationToken);
        await db.Set<SagaInstance>().ExecuteDeleteAsync(cancellationToken);
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>A clock that moves only when a test says so.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

[CollectionDefinition(Name)]
public sealed class SagaCollection : ICollectionFixture<SagaFixture>
{
    public const string Name = "Sagas";
}
