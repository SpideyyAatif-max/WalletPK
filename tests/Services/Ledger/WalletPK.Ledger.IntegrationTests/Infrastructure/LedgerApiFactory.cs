using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Ledger service against real PostgreSQL and Redis in throwaway Docker containers. The
/// database is migrated on startup, so the triggers and check constraints under test are the real ones.
/// The clock can be moved forward (hold expiry), and the background sweeper is off so tests decide when it runs.
/// Access tokens are signed with a test RSA key, and the Identity limits service is a fake tests can steer.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("walletpk_ledger")
        .WithUsername("walletpk")
        .WithPassword("walletpk_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    private readonly RSA _rsa = RSA.Create(2048);

    public LedgerApiFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>Stands in for the Identity service's Limits gRPC API.</summary>
    public FakeLimitsGateway Limits { get; } = new();

    /// <summary>The service's clock: starts at the real time and only moves when a test advances it.</summary>
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>For tests that go around the application on purpose to prove the database's own guards.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        _rsa.Dispose();
    }

    /// <summary>An access token like the Identity service issues. Valid around both the real time and the test clock.</summary>
    public string CreateToken(UserId userId, string? phoneNumber, string role = "customer", string deviceId = "0193f1c4-0000-7000-8000-000000000001")
    {
        var now = DateTime.UtcNow;
        var clock = Clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["device_id"] = deviceId,
            ["kyc_tier"] = "L1",
            ["role"] = role,
        };
        if (phoneNumber is not null)
        {
            claims["phone_number"] = phoneNumber;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "walletpk-identity",
            Audience = "walletpk-api",
            IssuedAt = (now < clock ? now : clock).AddMinutes(-5),
            NotBefore = (now < clock ? now : clock).AddMinutes(-5),
            Expires = (now > clock ? now : clock).AddMinutes(30),
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>An HTTP client signed in as <paramref name="userId"/>.</summary>
    public HttpClient CreateClientFor(UserId userId, string? phoneNumber, string role = "customer")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId, phoneNumber, role));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:LedgerDb"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Database:MigrateOnStartup"] = "true",
            ["Ledger:Maintenance:Enabled"] = "false",
            ["Ledger:Locks:WaitMilliseconds"] = "15000",

            // Tests seal the audit chain and run the invariant checks themselves, when they want to.
            ["Ledger:Audit:SealingEnabled"] = "false",
            ["Ledger:Invariants:Enabled"] = "false",
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<ILimitsGateway>();
            services.AddSingleton<ILimitsGateway>(Limits);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Use the test key directly instead of downloading keys from the Identity service.
                var configuration = new OpenIdConnectConfiguration { Issuer = "walletpk-identity" };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        });
    }
}

/// <summary>A clock tests can move forward; thread-safe because the race tests post in parallel.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>All Ledger integration tests share one service and one database container.</summary>
[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "Ledger API";
}

/// <summary>
/// A stand-in for Identity's limits: allows everything unless a test says otherwise for a user, and
/// remembers what was reserved and released so tests can check the compensation.
/// </summary>
public sealed class FakeLimitsGateway : ILimitsGateway
{
    private readonly ConcurrentDictionary<UserId, LimitDecisionResult> _refusals = new();
    private readonly ConcurrentDictionary<UserId, bool> _unavailable = new();

    public ConcurrentDictionary<TransactionId, Money> Reserved { get; } = new();

    public ConcurrentDictionary<TransactionId, bool> Released { get; } = new();

    public void Refuse(UserId userId, string decision, string reason) =>
        _refusals[userId] = new LimitDecisionResult(false, decision, reason);

    public void MakeUnavailable(UserId userId) => _unavailable[userId] = true;

    public void MakeAvailable(UserId userId) => _unavailable.TryRemove(userId, out _);

    public Task<LimitDecisionResult> ReserveAsync(
        TransactionId transactionId, UserId userId, JournalType type, Money amount, string? deviceId, CancellationToken cancellationToken)
    {
        if (_unavailable.ContainsKey(userId))
        {
            throw new LimitsUnavailableException("Identity is down (test).");
        }

        if (_refusals.TryGetValue(userId, out var refusal))
        {
            return Task.FromResult(refusal);
        }

        Reserved[transactionId] = amount;
        return Task.FromResult(new LimitDecisionResult(true, "Allowed", "Within limits."));
    }

    public Task ReleaseAsync(TransactionId transactionId, CancellationToken cancellationToken)
    {
        Released[transactionId] = true;
        return Task.CompletedTask;
    }
}
