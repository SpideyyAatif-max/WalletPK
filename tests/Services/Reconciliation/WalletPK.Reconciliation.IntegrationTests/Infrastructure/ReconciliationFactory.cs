extern alias mock;

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Reconciliation.Application;
using WalletPK.Reconciliation.Domain;
using MockProgram = mock::Program;

namespace WalletPK.Reconciliation.IntegrationTests.Infrastructure;

/// <summary>
/// The Reconciliation service under test, wired to real PostgreSQL and Kafka (containers) and the real mock
/// providers in-process: their settlement files are built from the operations the tests make there.
/// The nightly schedule is off; tests start runs over the API.
/// </summary>
public sealed class ReconciliationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ProviderKey = "test-provider-key-0123456789";
    public const string AdminKey = "test-admin-key-0123456789";

    /// <summary>Configured but unknown to the providers: its file is never there.</summary>
    public const string NoFileParty = "bank:NOFILE";

    // One PostgreSQL for both: the mock providers keep their tables in the "mock" schema.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_reconciliation").WithUsername("walletpk").WithPassword("walletpk_test").Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.3.1").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public ReconciliationFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
        Providers = new MockProvidersHost(this);
    }

    public RsaSecurityKey SigningKey { get; }

    public MockProvidersHost Providers { get; }

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());
        _ = Providers.Server;
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Providers.DisposeAsync();
        await _postgres.DisposeAsync();
        await _kafka.DisposeAsync();
        _rsa.Dispose();
    }

    public HttpClient ClientFor(string role, Guid? userId = null)
    {
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(60),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = (userId ?? Guid.NewGuid()).ToString(),
                ["device_id"] = "0193f1c4-0000-7000-8000-000000000001",
                ["kyc_tier"] = "L3",
                ["role"] = role,
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task PublishAsync(IntegrationEvent integrationEvent)
    {
        var envelope = new JsonEventSerializer(Services.GetRequiredService<IntegrationEventRegistry>()).Wrap(integrationEvent, "reconciliation-tests");
        using var publisher = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = _kafka.GetBootstrapAddress(), ClientId = "reconciliation-tests" }, NullLogger<KafkaEventPublisher>.Instance);
        await publisher.PublishAsync(envelope, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs SQL against the service's database (to tamper with the audit log, for one).</summary>
    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReconciliationDb"] = _postgres.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:EnsureTopics"] = "true",
            ["Reconciliation:Schedule:Enabled"] = "false",
            ["Reconciliation:Schedule:PollSeconds"] = "1",
            ["Providers:Settlement:BaseUrl"] = "http://mock-providers.test/",
            ["Providers:Settlement:ApiKey"] = ProviderKey,
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        };
        var parties = Parties.Default.Append(NoFileParty).ToList();
        for (var index = 0; index < parties.Count; index++)
        {
            settings[string.Create(CultureInfo.InvariantCulture, $"Reconciliation:Parties:{index}")] = parties[index];
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(nameof(ISettlementSource))
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
}

/// <summary>A top-up the mock network applied.</summary>
public sealed record TelcoTopUp(Guid TopUpId, string TelcoReference, long AmountPaisa, string Msisdn);

/// <summary>The mock providers service, in-process.</summary>
public sealed class MockProvidersHost(ReconciliationFactory service) : WebApplicationFactory<MockProgram>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", ReconciliationFactory.AdminKey);
        return client;
    }

    public HttpClient Client()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ReconciliationFactory.ProviderKey);
        return client;
    }

    /// <summary>Airtime on <paramref name="telco"/>, applied at once; <paramref name="prefix"/> picks the network (e.g. "0300" for Jazz).</summary>
    public async Task<TelcoTopUp> TopUpAsync(string telco, string prefix, long amountPaisa)
    {
        using var client = Client();
        var id = Guid.NewGuid();
        var msisdn = string.Create(CultureInfo.InvariantCulture, $"{prefix}{Random.Shared.Next(1_000_000, 9_999_999)}");
        using var response = await client.PostAsJsonAsync(new Uri($"/telcos/{telco}/topups", UriKind.Relative), new { topUpId = id.ToString(), msisdn, amountPaisa }, Cancel);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return new TelcoTopUp(id, body.GetProperty("telcoReference").GetString()!, body.GetProperty("amountPaisa").GetInt64(), msisdn);
    }

    public async Task PlantAsync(string kind, string code, DateOnly date, object plan)
    {
        using var client = Admin();
        using var response = await client.PutAsJsonAsync(new Uri($"/admin/settlement/{kind}/{code}/{date:yyyy-MM-dd}/mismatches", UriKind.Relative), plan, Cancel);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> InjectedAsync(string kind, string code, DateOnly date)
    {
        using var client = Admin();
        return await client.GetFromJsonAsync<JsonElement>(new Uri($"/admin/settlement/{kind}/{code}/{date:yyyy-MM-dd}/injected", UriKind.Relative), Cancel);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MockProvidersDb"] = service.PostgresConnectionString,
            ["MockProviders:ClientApiKeys:0"] = ReconciliationFactory.ProviderKey,
            ["MockProviders:AdminApiKey"] = ReconciliationFactory.AdminKey,
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
public sealed class ReconciliationCollection : ICollectionFixture<ReconciliationFactory>
{
    public const string Name = "Reconciliation";
}
