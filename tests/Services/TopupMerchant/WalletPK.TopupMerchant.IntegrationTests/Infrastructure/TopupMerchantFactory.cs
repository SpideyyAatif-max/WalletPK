extern alias mock;

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
using WalletPK.TopupMerchant.Application.TopUps;
using WalletPK.Messaging.Kafka;
using MockProgram = mock::Program;

namespace WalletPK.TopupMerchant.IntegrationTests.Infrastructure;

/// <summary>
/// The Top-up &amp; Merchant service under test, wired to real PostgreSQL and Kafka (containers), the real
/// mock networks in-process (through an in-memory handler), and a <see cref="FakeLedger"/> answering hold
/// and merchant commands over Kafka. Timings are shortened; the service's clock can jump ahead for the rules
/// measured in minutes and hours.
/// </summary>
public sealed class TopupMerchantFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ProviderKey = "test-provider-key-0123456789";
    public const string AdminKey = "test-admin-key-0123456789";
    public const string QrKey = "test-qr-signing-key-0123456789-abcdefghij";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_topup_merchant").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly PostgreSqlContainer _mockPostgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_mockproviders").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public TopupMerchantFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
        Providers = new MockTelcosHost(this);
    }

    public RsaSecurityKey SigningKey { get; }

    public MockTelcosHost Providers { get; }

    public FakeLedger Ledger { get; } = new();

    public ShiftedClock Clock { get; } = new();

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public string MockConnectionString => _mockPostgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mockPostgres.StartAsync(), _kafka.StartAsync());
        _ = Server;
        _ = Providers.Server;
        Ledger.Start(KafkaBootstrapServers);
    }

    public override async ValueTask DisposeAsync()
    {
        await Ledger.DisposeAsync();
        await Providers.DisposeAsync();
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _mockPostgres.DisposeAsync();
        await _kafka.DisposeAsync();
        _rsa.Dispose();
    }

    public string CreateToken(Guid userId, string role = "customer", string phone = "+923001234567")
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
                ["kyc_tier"] = "L1",
                ["role"] = role,
                ["phone_number"] = phone,
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpClient ClientFor(Guid userId, string role = "customer", string phone = "+923001234567")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId, role, phone));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TopupMerchantDb"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Outbox:Relay:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:StepTimeoutSeconds"] = "20",
            ["Sagas:Timers:LeaseSeconds"] = "30",
            ["Sagas:Timers:RetryDelaySeconds"] = "1",

            // Seconds instead of minutes; the rules in minutes and hours stay (tests move the clock).
            ["TopUps:LedgerReplyTimeoutSeconds"] = "3",
            ["TopUps:MaxLedgerResends"] = "3",
            ["TopUps:MaxAttempts"] = "30",
            ["TopUps:RetryInitialSeconds"] = "1",
            ["TopUps:RetryMaxSeconds"] = "2",
            ["TopUps:VerifyDelaySeconds"] = "1",
            ["TopUps:VerifyMaxSeconds"] = "2",
            ["Merchants:Payments:LedgerReplyTimeoutSeconds"] = "3",
            ["Merchants:Payments:MaxLedgerResends"] = "3",
            ["Merchants:Qr:SigningKey"] = QrKey,
            ["Merchants:Settlement:Enabled"] = "false",
            ["Merchants:Settlement:ResendMinutes"] = "10",

            ["Providers:Telcos:BaseUrl"] = "http://mock-providers.test/",
            ["Providers:Telcos:ApiKey"] = ProviderKey,
            ["Providers:Telcos:Resilience:AttemptTimeoutMilliseconds"] = "1000",
            ["Providers:Telcos:Resilience:TotalTimeoutMilliseconds"] = "4000",
            ["Providers:Telcos:Resilience:RetryBaseDelayMilliseconds"] = "50",
            ["Providers:Telcos:Resilience:MinimumThroughput"] = "3",
            ["Providers:Telcos:Resilience:SamplingSeconds"] = "10",
            ["Providers:Telcos:Resilience:BreakSeconds"] = "2",

            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.AddHttpClient(nameof(ITelcoGateway))
                .ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(() => Providers.Server.CreateHandler()));

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

/// <summary>The mock providers service (its mobile networks), in-process.</summary>
public sealed class MockTelcosHost(TopupMerchantFactory service) : WebApplicationFactory<MockProgram>
{
    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TopupMerchantFactory.AdminKey);
        return client;
    }

    public HttpClient Client()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TopupMerchantFactory.ProviderKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MockProvidersDb"] = service.MockConnectionString,
            ["MockProviders:ClientApiKeys:0"] = TopupMerchantFactory.ProviderKey,
            ["MockProviders:AdminApiKey"] = TopupMerchantFactory.AdminKey,
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));
    }
}

/// <summary>Sends requests to a handler created on first use.</summary>
public sealed class ForwardingHandler(Func<HttpMessageHandler> target) : HttpMessageHandler
{
    private readonly Lazy<HttpMessageInvoker> _invoker = new(() => new HttpMessageInvoker(target(), disposeHandler: false));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.Value.SendAsync(request, cancellationToken);
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
public sealed class TopupMerchantCollection : ICollectionFixture<TopupMerchantFactory>
{
    public const string Name = "TopupMerchant";
}
