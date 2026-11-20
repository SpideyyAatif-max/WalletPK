using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WalletPK.Messaging.Kafka;

namespace WalletPK.Agents.IntegrationTests.Infrastructure;

/// <summary>
/// The Agent Network service under test, wired to real PostgreSQL and Kafka (containers) and a
/// <see cref="FakeLedger"/> answering agent commands over Kafka. Applications need an operator's approval
/// (AutoApprove off), timings are shortened, and the service's clock can jump ahead.
/// </summary>
public sealed class AgentsFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string CodeKey = "test-cash-out-code-key-0123456789-abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_agents").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public AgentsFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
    }

    public RsaSecurityKey SigningKey { get; }

    public FakeLedger Ledger { get; } = new();

    public ShiftedClock Clock { get; } = new();

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());
        _ = Server;
        Ledger.Start(KafkaBootstrapServers);
    }

    public override async ValueTask DisposeAsync()
    {
        await Ledger.DisposeAsync();
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _kafka.DisposeAsync();
        _rsa.Dispose();
    }

    public string CreateToken(Guid userId, string role, string phone, string kycTier)
    {
        var now = DateTime.UtcNow;
        var clock = Clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = (now < clock ? now : clock).AddMinutes(-5),
            NotBefore = (now < clock ? now : clock).AddMinutes(-5),
            Expires = (now > clock ? now : clock).AddMinutes(60),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["device_id"] = "0193f1c4-0000-7000-8000-000000000001",
                ["kyc_tier"] = kycTier,
                ["role"] = role,
                ["phone_number"] = phone,
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A signed-in user: <paramref name="phone"/> is their wallet number (E.164).</summary>
    public HttpClient ClientFor(Guid userId, string phone, string role = "customer", string kycTier = "L1")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId, role, phone, kycTier));
        return client;
    }

    /// <summary>A new random wallet number.</summary>
    public static string NewPhone() => $"+92300{Random.Shared.Next(1_000_000, 10_000_000)}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AgentsDb"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Outbox:Relay:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:StepTimeoutSeconds"] = "20",
            ["Sagas:Timers:LeaseSeconds"] = "30",
            ["Sagas:Timers:RetryDelaySeconds"] = "1",

            ["Agents:MinimumKycTier"] = "L1",
            ["Agents:AutoApprove"] = "false",
            ["Agents:CashOutCodeKey"] = CodeKey,
            ["Agents:Transactions:LedgerReplyTimeoutSeconds"] = "3",
            ["Agents:Transactions:MaxLedgerResends"] = "3",
            ["Agents:Payouts:Enabled"] = "false",
            ["Agents:Payouts:ResendMinutes"] = "10",

            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = "walletpk-identity" };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        });
    }

    /// <summary>Reads <paramref name="topic"/> from the start until <paramref name="done"/> holds.</summary>
    public List<(string EventType, JsonElement Payload)> ReadUntil(string topic, Func<IReadOnlyList<(string EventType, JsonElement Payload)>, bool> done, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(done);
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = KafkaBootstrapServers,
            GroupId = $"test-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic);

        var read = new List<(string, JsonElement)>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is not null && KafkaMessages.TryFromMessage(result.Topic, result.Message, out var envelope, out _))
                {
                    read.Add((envelope!.EventType, JsonDocument.Parse(envelope.Payload).RootElement.Clone()));
                }

                if (done(read))
                {
                    return read;
                }
            }
        }
        finally
        {
            consumer.Close();
        }

        throw new TimeoutException($"Did not find the expected messages on {topic}; read {read.Count}.");
    }
}

/// <summary>The real time plus an offset tests can move forward.</summary>
public sealed class ShiftedClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}

[CollectionDefinition(Name)]
public sealed class AgentsCollection : ICollectionFixture<AgentsFactory>
{
    public const string Name = "Agents";
}
