extern alias mock;

using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WalletPK.Messaging.Kafka;
using WalletPK.Payments.Application.Raast;
using WalletPK.Web.Security;
using MockProgram = mock::Program;

namespace WalletPK.Payments.IntegrationTests.Infrastructure;

/// <summary>
/// The Payments service under test, wired to:
/// <list type="bullet">
/// <item>real PostgreSQL and Kafka in containers;</item>
/// <item>the real mock providers, in-process: Payments' Raast client calls it, and its signed callbacks
/// are delivered to Payments' webhook (both through in-memory handlers, no sockets);</item>
/// <item>a <see cref="FakeLedger"/> answering hold commands over Kafka.</item>
/// </list>
/// Timings are shortened (seconds instead of minutes), and the service's clock can jump ahead for the
/// rules that are measured in hours.
/// </summary>
public sealed class PaymentsFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ProviderKey = "test-provider-key-0123456789";
    public const string AdminKey = "test-admin-key-0123456789";
    public const string RaastSecret = "test-raast-webhook-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_payments").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly PostgreSqlContainer _mockPostgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_mockproviders").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public PaymentsFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
        Providers = new MockProvidersHost(this);
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>The mock providers (Raast, banks), in-process.</summary>
    public MockProvidersHost Providers { get; }

    public FakeLedger Ledger { get; } = new();

    /// <summary>Real time plus an offset tests can move forward.</summary>
    public ShiftedClock Clock { get; } = new();

    /// <summary>Switch off to lose Raast's callbacks (Payments must then find out by asking).</summary>
    public CallbackRelay Callbacks { get; } = new();

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _mockPostgres.StartAsync(), _kafka.StartAsync());
        Callbacks.Target = () => Server.CreateHandler();

        // Start Payments (creates the topics), then the stand-in Ledger.
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

    public string MockConnectionString => _mockPostgres.GetConnectionString();

    /// <summary>An access token like the Identity service issues, valid around both the real time and the service's clock.</summary>
    public string CreateToken(Guid userId, string? phoneNumber = null, string deviceId = "0193f1c4-0000-7000-8000-000000000001")
    {
        var now = DateTime.UtcNow;
        var clock = Clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = (now < clock ? now : clock).AddMinutes(-5),
            NotBefore = (now < clock ? now : clock).AddMinutes(-5),
            Expires = (now > clock ? now : clock).AddMinutes(30),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["device_id"] = deviceId,
                ["kyc_tier"] = "L1",
                ["role"] = "customer",
                ["phone_number"] = phoneNumber ?? PhoneOf(userId),
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>MPIN confirmation is off for most tests; MpinConfirmationTests turns it on (read on every request).</summary>
    public MpinConfirmationOptions MpinConfirmation { get; } = new() { Required = false };

    /// <summary>What Identity's POST /api/identity/mpin/confirm returns, signed with the test key.</summary>
    public string CreateMpinConfirmation(Guid userId, string binding, string deviceId = "0193f1c4-0000-7000-8000-000000000001", string audience = "walletpk-confirmation", TimeSpan? lifetime = null)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = audience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(5)),
            Claims = new Dictionary<string, object> { ["sub"] = userId.ToString(), ["device_id"] = deviceId, ["binding"] = binding },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpClient ClientFor(Guid userId, string? phoneNumber = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId, phoneNumber));
        return client;
    }

    /// <summary>Each test user's own mobile number (+92300 and 7 digits from the user ID).</summary>
    public static string PhoneOf(Guid userId) =>
        "+92300" + (Math.Abs(BitConverter.ToInt32(userId.ToByteArray(), 0)) % 10_000_000).ToString("0000000", CultureInfo.InvariantCulture);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PaymentsDb"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Outbox:Relay:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:PollIntervalMilliseconds"] = "50",
            ["Sagas:Timers:StepTimeoutSeconds"] = "20",
            ["Sagas:Timers:LeaseSeconds"] = "30",
            ["Sagas:Timers:RetryDelaySeconds"] = "1",

            // Seconds instead of minutes; hours stay hours (tests move the clock for those).
            ["Payments:BankTransfers:LedgerReplyTimeoutSeconds"] = "3",
            ["Payments:BankTransfers:MaxLedgerResends"] = "3",
            ["Payments:BankTransfers:QueueRetryInitialSeconds"] = "1",
            ["Payments:BankTransfers:QueueRetryMaxSeconds"] = "2",
            ["Payments:BankTransfers:VerifyDelaySeconds"] = "1",
            ["Payments:BankTransfers:StatusCheckInitialSeconds"] = "1",
            ["Payments:BankTransfers:StatusCheckMaxSeconds"] = "2",
            ["Payments:BankTransfers:MaxQueueHours"] = "1",
            ["Payments:BankTransfers:MaxWaitHours"] = "2",
            ["Payments:BankTransfers:HoldLifetimeHours"] = "4",

            ["Providers:Raast:BaseUrl"] = "http://mock-providers.test/",
            ["Providers:Raast:ApiKey"] = ProviderKey,
            ["Providers:Raast:Resilience:AttemptTimeoutMilliseconds"] = "1000",
            ["Providers:Raast:Resilience:TotalTimeoutMilliseconds"] = "4000",
            ["Providers:Raast:Resilience:RetryBaseDelayMilliseconds"] = "50",
            ["Providers:Raast:Resilience:MinimumThroughput"] = "3",
            ["Providers:Raast:Resilience:SamplingSeconds"] = "10",
            ["Providers:Raast:Resilience:BreakSeconds"] = "2",
            ["Webhooks:Raast:Secret"] = RaastSecret,

            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IOptions<MpinConfirmationOptions>>(Options.Create(MpinConfirmation));

            // Raast calls go to the in-process mock providers.
            services.AddHttpClient(nameof(IRaastGateway))
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

/// <summary>The mock providers service, with its callbacks delivered to Payments' webhook.</summary>
public sealed class MockProvidersHost(PaymentsFactory payments) : WebApplicationFactory<MockProgram>
{
    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", PaymentsFactory.AdminKey);
        return client;
    }

    public HttpClient Client()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentsFactory.ProviderKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MockProvidersDb"] = payments.MockConnectionString,
            ["MockProviders:ClientApiKeys:0"] = PaymentsFactory.ProviderKey,
            ["MockProviders:AdminApiKey"] = PaymentsFactory.AdminKey,
            ["MockProviders:Raast:SettleDelayMinMilliseconds"] = "100",
            ["MockProviders:Raast:SettleDelayMaxMilliseconds"] = "300",
            ["MockProviders:Raast:CallbackUrl"] = "http://payments.test/webhooks/raast/transfer-status",
            ["MockProviders:Raast:CallbackSecret"] = PaymentsFactory.RaastSecret,
            ["MockProviders:Callbacks:PollMilliseconds"] = "50",
            ["MockProviders:Callbacks:MaxAttempts"] = "2",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
            services.AddHttpClient(mock::WalletPK.MockProviders.Callbacks.CallbackSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => payments.Callbacks)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }
}

/// <summary>Sends requests to a handler created on first use (the two in-process services need each other).</summary>
public sealed class ForwardingHandler(Func<HttpMessageHandler> target) : HttpMessageHandler
{
    private readonly Lazy<HttpMessageInvoker> _invoker = new(() => new HttpMessageInvoker(target(), disposeHandler: false));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.Value.SendAsync(request, cancellationToken);
}

/// <summary>Delivers Raast's callbacks to Payments, unless switched off (then the mock sees 503 and gives up after its retries).</summary>
public sealed class CallbackRelay : HttpMessageHandler
{
    private HttpMessageInvoker? _invoker;

    public Func<HttpMessageHandler>? Target { get; set; }

    public bool Enabled { get; set; } = true;

    public int Delivered => _delivered;

    private int _delivered;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Enabled || Target is null)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }

        _invoker ??= new HttpMessageInvoker(Target(), disposeHandler: false);
        var response = await _invoker.SendAsync(request, cancellationToken);
        Interlocked.Increment(ref _delivered);
        return response;
    }

    // Lives as long as the test run; HttpClientFactory must not dispose it.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>The real time plus an offset (timers due in seconds still fire; tests can jump hours ahead).</summary>
public sealed class ShiftedClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}

/// <summary>Valid Pakistani IBANs for the mock banks (any account number is an account there).</summary>
public static class TestIbans
{
    /// <summary>An active account at the bank with IBAN code <paramref name="bankCode"/> (HABB, MEZN, UNIL, MUCB).</summary>
    public static string NewActive(string bankCode)
    {
        var number = Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999).ToString(CultureInfo.InvariantCulture)[..12] + "1234";
        return For(bankCode, number);
    }

    /// <summary>Ends in 0000: the bank rejects transfers (account closed).</summary>
    public static string Closed(string bankCode) => For(bankCode, "123456780000"[..12] + "0000");

    public static string For(string bankCode, string accountNumber)
    {
        accountNumber = accountNumber.PadLeft(16, '0')[^16..];
        var digits = string.Concat((bankCode + accountNumber + "PK00").Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        var check = 98 - (int)(BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97);
        return $"PK{check:00}{bankCode}{accountNumber}";
    }
}

/// <summary>All Payments tests share one service, one mock providers service and one Ledger stand-in.</summary>
[CollectionDefinition(Name)]
public sealed class PaymentsCollection : ICollectionFixture<PaymentsFactory>
{
    public const string Name = "Payments";
}
