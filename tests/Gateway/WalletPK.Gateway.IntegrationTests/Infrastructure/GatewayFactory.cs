using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace WalletPK.Gateway.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real gateway in memory. Every cluster points at one <see cref="EchoBackend"/>, tokens are
/// signed with a test RSA key (instead of fetching keys from the Identity service), and external
/// telemetry exporters are switched off.
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "walletpk-identity";
    public const string Audience = "walletpk-api";
    public const string RaastSecret = "test-raast-secret";
    public const string AllowedOrigin = "http://localhost:3000";

    private static readonly string[] _clusters =
    [
        "identity", "ledger", "payments", "bills", "topup-merchant",
        "agents", "fraud", "notifications", "reconciliation", "simulation",
    ];

    private readonly RSA _rsa = RSA.Create(2048);
    private EchoBackend? _backend;

    public GatewayFactory()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-signing-key" };
    }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>Settings a derived factory can change (for example a lower rate limit).</summary>
    protected virtual IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>();

    public async ValueTask InitializeAsync() => _backend = await EchoBackend.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }

        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates an access token like the Identity service will issue.</summary>
    public string CreateToken(
        string userId,
        string role = "customer",
        string deviceId = "pixel-8",
        string kycTier = "L1",
        DateTime? expires = null,
        string audience = Audience,
        SecurityKey? signingKey = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(15);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = expiry.AddMinutes(-30),
            NotBefore = expiry.AddMinutes(-30),
            Expires = expiry,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId,
                ["device_id"] = deviceId,
                ["kyc_tier"] = kycTier,
                ["role"] = role,
            },
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(BuildSettings()));
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Use the test key directly instead of downloading keys from the Identity service.
                // The static manager matters: without it every request first tries to download the keys
                // from the (absent) Identity service and only then falls back to the test key. On Windows a
                // refused connection takes ~2 s per address (IPv6 + IPv4), so each request cost ~4 s.
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    private Dictionary<string, string?> BuildSettings()
    {
        var backendUrl = _backend?.Url ?? throw new InvalidOperationException("The echo backend has not started.");

        var settings = new Dictionary<string, string?>
        {
            ["Observability:WriteToConsole"] = "false",
            ["Observability:WriteToSeq"] = "false",
            ["Observability:ExportTraces"] = "false",
            ["Observability:ExportMetrics"] = "false",
            ["Webhooks:Secrets:raast"] = RaastSecret,
            ["Cors:AllowedOrigins:0"] = AllowedOrigin,
        };

        foreach (var cluster in _clusters)
        {
            settings[$"ReverseProxy:Clusters:{cluster}:Destinations:d1:Address"] = backendUrl;
        }

        foreach (var (key, value) in ExtraSettings)
        {
            settings[key] = value;
        }

        return settings;
    }
}

/// <summary>Same gateway with a tiny login rate limit, so the limit can be hit in a test.</summary>
public sealed class RateLimitedGatewayFactory : GatewayFactory
{
    protected override IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["RateLimiting:AuthPermitLimit"] = "3",
        ["RateLimiting:AuthWindowSeconds"] = "60",
    };
}
