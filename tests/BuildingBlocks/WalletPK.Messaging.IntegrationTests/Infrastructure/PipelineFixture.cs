using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Messaging.IntegrationTests.Infrastructure;

[IntegrationEvent("test.something_happened", KafkaTopics.LedgerEvents)]
public sealed record SomethingHappened(string Key, int Number) : IntegrationEvent
{
    public override string GetPartitionKey() => Key;
}

/// <summary>A row a handler writes, to prove its changes commit (or roll back) with the inbox row.</summary>
public sealed class Note
{
    public long Id { get; set; }

    public string Text { get; set; } = null!;
}

/// <summary>The smallest service database: an outbox, an inbox and a table handlers write to.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
    }
}

/// <summary>Records every envelope; can be told to fail some of them.</summary>
public sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<EventEnvelope> Published { get; } = new();

    public Func<EventEnvelope, bool> ShouldFail { get; set; } = _ => false;

    /// <summary>Like the Kafka producer: queued in call order, acknowledged later.</summary>
    public async Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var fail = ShouldFail(envelope);
        if (!fail)
        {
            Published.Enqueue(envelope);
        }

        await Task.Yield();
        if (fail)
        {
            throw new InvalidOperationException("Broker unavailable (test)");
        }
    }
}

/// <summary>Writes a note per event; throws for events whose number is negative.</summary>
public sealed class NoteWritingHandler(TestDbContext db) : IIntegrationEventHandler<SomethingHappened>
{
    public Task HandleAsync(SomethingHappened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        db.Notes.Add(new Note { Text = $"{integrationEvent.Key}:{integrationEvent.Number}" });
        if (integrationEvent.Number < 0)
        {
            throw new InvalidOperationException("Handler failed (test)");
        }

        return Task.CompletedTask;
    }
}

/// <summary>PostgreSQL and Kafka in containers, shared by the tests in this assembly (one collection).</summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.0").Build();

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public IntegrationEventRegistry Registry { get; } = new IntegrationEventRegistry().Register<SomethingHappened>();

    public JsonEventSerializer Serializer => new(Registry);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());
        await using var db = NewDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _kafka.DisposeAsync();
    }

    public TestDbContext NewDbContext() => new(new DbContextOptionsBuilder<TestDbContext>()
        .UseNpgsql(_postgres.GetConnectionString())
        .UseSnakeCaseNamingConvention()
        .Options);

    public OutboxRelay<TestDbContext> Relay(TestDbContext db, IEventPublisher publisher, int batchSize = 100) =>
        new(db, publisher, Clock, new OutboxRelayOptions { BatchSize = batchSize, MaxBackoffSeconds = 60 }, NullLogger<OutboxRelay<TestDbContext>>.Instance);

    /// <summary>Writes events to the outbox as a service's interceptor would, oldest first.</summary>
    public async Task<List<EventEnvelope>> AddToOutboxAsync(int count, string key = "k")
    {
        var serializer = Serializer;
        var envelopes = Enumerable.Range(1, count).Select(number => serializer.Wrap(new SomethingHappened(key, number))).ToList();

        await using var db = NewDbContext();
        foreach (var envelope in envelopes)
        {
            db.Set<OutboxMessage>().Add(OutboxMessage.From(envelope, Clock.GetUtcNow()));
            Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        await db.SaveChangesAsync();
        return envelopes;
    }

    /// <summary>A DI scope like a service's, with the inbox processor and the note-writing handler.</summary>
    public ServiceProvider InboxServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Serializer);
        services.AddScoped(_ => NewDbContext());
        services.AddIntegrationEventHandler<SomethingHappened, NoteWritingHandler>();
        services.AddScoped<IEnvelopeProcessor, InboxEnvelopeProcessor<TestDbContext>>();
        return services.BuildServiceProvider();
    }
}

/// <summary>A clock that moves only when a test says so (and a millisecond per outbox row).</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture>
{
    public const string Name = "Event pipeline";
}
