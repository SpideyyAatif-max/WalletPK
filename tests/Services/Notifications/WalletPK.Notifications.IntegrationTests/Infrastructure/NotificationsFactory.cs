extern alias mock;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Notifications.Application;
using MockProgram = mock::Program;

namespace WalletPK.Notifications.IntegrationTests.Infrastructure;

/// <summary>
/// The Notifications service under test, wired to real PostgreSQL, Redis and Kafka (containers) and the real
/// mock SMS and email gateways in-process. Retries are seconds, not minutes.
/// </summary>
public sealed class NotificationsFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ProviderKey = "test-provider-key-0123456789";
    public const string AdminKey = "test-admin-key-0123456789";

    // One PostgreSQL for both: the mock providers keep their tables in the "mock" schema.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_notifications").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public NotificationsFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
        Providers = new MockGatewaysHost(this);
    }

    public RsaSecurityKey SigningKey { get; }

    public MockGatewaysHost Providers { get; }

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());
        _ = Providers.Server;
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Providers.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _kafka.DisposeAsync();
        _rsa.Dispose();
    }

    public string CreateToken(Guid userId, string role = "customer", string? phone = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["device_id"] = "0193f1c4-0000-7000-8000-000000000001",
            ["kyc_tier"] = "L1",
            ["role"] = role,
        };
        if (phone is not null)
        {
            claims["phone_number"] = phone;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(60),
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpClient ClientFor(Guid userId, string role = "customer", string? phone = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId, role, phone));
        return client;
    }

    /// <summary>Wraps an event as the producing service would (a new event ID each time).</summary>
    public EventEnvelope Wrap(IntegrationEvent integrationEvent) =>
        new JsonEventSerializer(Services.GetRequiredService<IntegrationEventRegistry>()).Wrap(integrationEvent, "notifications-tests");

    public Task PublishAsync(IntegrationEvent integrationEvent) => PublishAsync(Wrap(integrationEvent));

    /// <summary>Publishes an envelope; publishing the same one twice is a Kafka redelivery.</summary>
    public async Task PublishAsync(EventEnvelope envelope)
    {
        using var publisher = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = KafkaBootstrapServers, ClientId = "notifications-tests" }, NullLogger<KafkaEventPublisher>.Instance);
        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);
    }

    /// <summary>A browser tab connected to a hub on <paramref name="host"/> (this instance unless given).</summary>
    public async Task<HubTab> OpenAsync(string path, Guid userId, string role = "customer", WebApplicationFactory<Program>? host = null)
    {
        var tab = new HubTab((host ?? this).Server, path, CreateToken(userId, role));
        await tab.StartAsync();
        return tab;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NotificationsDb"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Outbox:Relay:PollIntervalMilliseconds"] = "50",

            ["Notifications:StartFromLatest"] = "false",
            ["Notifications:SenderPollMilliseconds"] = "100",
            ["Notifications:RetryInitialSeconds"] = "1",
            ["Notifications:RetryMaxSeconds"] = "2",
            ["Notifications:MaxAttempts"] = "10",

            ["Providers:Messaging:BaseUrl"] = "http://mock-providers.test/",
            ["Providers:Messaging:ApiKey"] = ProviderKey,
            ["Providers:Messaging:Resilience:AttemptTimeoutMilliseconds"] = "500",
            ["Providers:Messaging:Resilience:TotalTimeoutMilliseconds"] = "2000",
            ["Providers:Messaging:Resilience:MaxRetries"] = "1",
            ["Providers:Messaging:Resilience:RetryBaseDelayMilliseconds"] = "50",
            ["Providers:Messaging:Resilience:MinimumThroughput"] = "100",

            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(nameof(IMessageGateway))
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

/// <summary>A browser tab: a hub connection over WebSockets, the token in the query string as the JS client sends it.</summary>
public sealed class HubTab : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public HubTab(TestServer server, string path, string token)
    {
        ArgumentNullException.ThrowIfNull(server);
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    uri.Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}".TrimStart('&');
                    return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            })
            .Build();
        foreach (var method in new[] { "EntryPosted", "NotificationReceived", "PaymentChanged", "Event" })
        {
            _connection.On<JsonElement>(method, payload => Pushed.Writer.TryWrite((method, payload)));
        }
    }

    public Channel<(string Method, JsonElement Payload)> Pushed { get; } = Channel.CreateUnbounded<(string, JsonElement)>();

    public Task StartAsync() => _connection.StartAsync(TestContext.Current.CancellationToken);

    /// <summary>The first push of <paramref name="method"/> that matches, skipping others.</summary>
    public async Task<JsonElement> NextAsync(string method, Func<JsonElement, bool>? match = null, int seconds = 20)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var (name, payload) = await Pushed.Reader.ReadAsync(timeout.Token);
            if (name == method && (match is null || match(payload)))
            {
                return payload;
            }
        }
    }

    /// <summary>Everything pushed so far, without waiting.</summary>
    public List<(string Method, JsonElement Payload)> Drain()
    {
        var all = new List<(string, JsonElement)>();
        while (Pushed.Reader.TryRead(out var item))
        {
            all.Add(item);
        }

        return all;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>The mock providers service (its SMS and email gateways), in-process.</summary>
public sealed class MockGatewaysHost(NotificationsFactory service) : WebApplicationFactory<MockProgram>
{
    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", NotificationsFactory.AdminKey);
        return client;
    }

    public HttpClient Client()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", NotificationsFactory.ProviderKey);
        return client;
    }

    /// <summary>The texts the gateway accepted for a number, newest first.</summary>
    public async Task<List<JsonElement>> TextsToAsync(string phone)
    {
        using var client = Client();
        return await client.GetFromJsonAsync<List<JsonElement>>(new Uri($"/sms/v1/messages?to={Uri.EscapeDataString(phone)}", UriKind.Relative), TestContext.Current.CancellationToken) ?? [];
    }

    /// <summary>The emails the gateway accepted for an address.</summary>
    public async Task<List<JsonElement>> EmailsToAsync(string address)
    {
        using var client = Client();
        return await client.GetFromJsonAsync<List<JsonElement>>(new Uri($"/email/v1/messages?to={Uri.EscapeDataString(address)}", UriKind.Relative), TestContext.Current.CancellationToken) ?? [];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MockProvidersDb"] = service.PostgresConnectionString,
            ["MockProviders:ClientApiKeys:0"] = NotificationsFactory.ProviderKey,
            ["MockProviders:AdminApiKey"] = NotificationsFactory.AdminKey,
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

[CollectionDefinition(Name)]
public sealed class NotificationsCollection : ICollectionFixture<NotificationsFactory>
{
    public const string Name = "Notifications";
}
