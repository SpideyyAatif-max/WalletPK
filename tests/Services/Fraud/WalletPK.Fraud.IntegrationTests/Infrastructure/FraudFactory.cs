using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Confluent.Kafka;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
using WalletPK.Contracts.Fraud.V1;
using WalletPK.Fraud.Application;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Fraud.IntegrationTests.Infrastructure;

/// <summary>A message read back from the test broker.</summary>
public sealed record ReadMessage(string Key, string EventType, JsonElement Payload);

/// <summary>
/// The Fraud &amp; Risk service under test, against real PostgreSQL, Redis and Kafka (containers). The gRPC
/// risk API is called in-process through the test server; compliance calls use signed test tokens.
/// </summary>
public sealed class FraudFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_fraud").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    private GrpcChannel? _channel;

    public FraudFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
    }

    public RsaSecurityKey SigningKey { get; }

    public string KafkaBootstrapServers => _kafka.GetBootstrapAddress();

    /// <summary>The gRPC risk API, as the Ledger calls it.</summary>
    public Risk.RiskClient Risk => new(_channel ??= GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() }));

    /// <summary>The Redis feature store, to set up users and devices the way the identity events would.</summary>
    public IFeatureStore Features => Services.GetRequiredService<IFeatureStore>();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());
        _ = Server;

        // The night rule follows the wall clock (1-5 am in Pakistan), so with it on these tests would score
        // differently at night. The unit tests cover it at fixed times.
        await using var scope = Services.CreateAsyncScope();
        var disabled = await scope.ServiceProvider.GetRequiredService<RuleService>()
            .UpdateAsync("odd-hours-large", name: null, description: null, definition: null, enabled: false, "integration-tests", CancellationToken.None);
        disabled.IsSuccess.ShouldBeTrue(disabled.IsFailure ? disabled.Error.Message : null);
    }

    public override async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _kafka.DisposeAsync();
        _rsa.Dispose();
    }

    public HttpClient ClientFor(string role, Guid? userId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = DateTime.UtcNow.AddMinutes(-5),
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddMinutes(60),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = (userId ?? Guid.NewGuid()).ToString(),
                ["device_id"] = "0193f1c4-0000-7000-8000-000000000001",
                ["kyc_tier"] = "L2",
                ["role"] = role,
                ["phone_number"] = "+923000000001",
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        }));
        return client;
    }

    /// <summary>Publishes an event the way another service's outbox relay would.</summary>
    public async Task PublishAsync(IntegrationEvent integrationEvent)
    {
        var envelope = new JsonEventSerializer(Services.GetRequiredService<IntegrationEventRegistry>()).Wrap(integrationEvent, "fraud-tests");
        using var publisher = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = KafkaBootstrapServers, ClientId = "fraud-tests" }, NullLogger<KafkaEventPublisher>.Instance);
        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FraudDb"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Outbox:Relay:PollIntervalMilliseconds"] = "50",
            ["Fraud:RulesPollSeconds"] = "60",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
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
    public List<ReadMessage> ReadUntil(string topic, Func<IReadOnlyList<ReadMessage>, bool> done, TimeSpan? timeout = null)
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

        var read = new List<ReadMessage>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is not null && KafkaMessages.TryFromMessage(result.Topic, result.Message, out var envelope, out _))
                {
                    read.Add(new ReadMessage(result.Message.Key, envelope!.EventType, JsonDocument.Parse(envelope.Payload).RootElement.Clone()));
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

[CollectionDefinition(Name)]
public sealed class FraudCollection : ICollectionFixture<FraudFactory>
{
    public const string Name = "Fraud";
}
