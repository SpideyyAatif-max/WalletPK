using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Application.Events;
using WalletPK.Identity.Application.Kyc;
using WalletPK.Identity.Application.Limits;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Application.Registration;
using WalletPK.Identity.Infrastructure.Health;
using WalletPK.Identity.Infrastructure.Nadra;
using WalletPK.Identity.Infrastructure.Persistence;
using WalletPK.Identity.Infrastructure.Persistence.Outbox;
using WalletPK.Persistence.Outbox;
using WalletPK.Identity.Infrastructure.Redis;
using WalletPK.Identity.Infrastructure.Security;
using WalletPK.Identity.Infrastructure.Sms;
using WalletPK.Messaging.Serialization;
using WalletPK.Web.Correlation;
using WalletPK.Web.Health;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Infrastructure;

public static class IdentityInfrastructureExtensions
{
    public const string DatabaseConnectionName = "IdentityDb";
    public const string RedisConnectionName = "Redis";

    /// <summary>
    /// Registers the Identity use cases and their adapters. Settings are read lazily from configuration,
    /// so every configuration source (appsettings, environment, test overrides) applies.
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ---- Options (validated at startup: a missing pepper must stop the service, not weaken it) ----
        services.AddOptions<OtpOptions>()
            .BindConfiguration(OtpOptions.SectionName)
            .Validate(options => options.Pepper.Length >= 32, "Otp:Pepper must be at least 32 characters.")
            .Validate(options => options.Length is >= 4 and <= 8, "Otp:Length must be between 4 and 8.")
            .ValidateOnStart();

        services.AddOptions<MpinHashingOptions>()
            .BindConfiguration(MpinHashingOptions.SectionName)
            .Validate(options => options.Pepper.Length >= 32, "MpinHashing:Pepper must be at least 32 characters.")
            .ValidateOnStart();

        services.AddOptions<RegistrationOptions>().BindConfiguration(RegistrationOptions.SectionName);

        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .Validate(options => options.AccessTokenMinutes is >= 1 and <= 60, "Auth:AccessTokenMinutes must be between 1 and 60.")
            .Validate(options => options.RefreshTokenDays >= 1, "Auth:RefreshTokenDays must be at least 1.")
            .Validate(options => options.MaxFailedMpinAttempts >= 1, "Auth:MaxFailedMpinAttempts must be at least 1.")
            .ValidateOnStart();

        services.AddOptions<JwtSigningOptions>()
            .BindConfiguration(JwtSigningOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer) && !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Issuer and Jwt:Audience are required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SigningKeyPath) || options.CreateSigningKeyIfMissing,
                "Jwt:SigningKeyPath is required (or Jwt:CreateSigningKeyIfMissing for development).")
            .ValidateOnStart();

        services.AddOptions<LimitsOptions>()
            .BindConfiguration(LimitsOptions.SectionName)
            .Validate(options => options.IsValid(), "Limits: each tier needs PerTransaction <= Daily <= Monthly, and higher tiers may not be lower.")
            .ValidateOnStart();

        services.AddOptions<EncryptionOptions>()
            .BindConfiguration(EncryptionOptions.SectionName)
            .Validate(options => options.HasValidKeys(), "Encryption:Keys must hold Base64 32-byte keys and include Encryption:ActiveKeyId.")
            .Validate(options => options.HasValidBlindIndexKey(), "Encryption:BlindIndexKey must be Base64 of at least 32 bytes.")
            .ValidateOnStart();

        // Application classes take plain option objects (they do not depend on Microsoft.Extensions.Options).
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<OtpOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MpinHashingOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<RegistrationOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AuthOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<JwtSigningOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<LimitsOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<EncryptionOptions>>().Value);

        // ---- Integration events: the contracts this service publishes, and the outbox writer ----
        services.AddSingleton(new IntegrationEventRegistry()
            .RegisterFromAssembly(typeof(WalletPK.Contracts.Identity.UserRegistered).Assembly, IdentityIntegrationEvents.ContractsNamespace));
        services.AddSingleton<JsonEventSerializer>();
        services.AddDomainEventOutbox<IdentityIntegrationEventMapper>(() => CorrelationContext.Current);

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<IdentityDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(provider.GetRequiredService<DomainEventOutboxInterceptor>()));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IKycProfileRepository, KycProfileRepository>();

        // ---- Redis ----
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            ConnectionMultiplexer.Connect(RequiredConnectionString(provider, RedisConnectionName)));
        services.AddSingleton<IOtpStore, RedisOtpStore>();
        services.AddSingleton<IRegistrationTokenStore, RedisRegistrationTokenStore>();
        services.AddSingleton<IDeviceChallengeStore, RedisDeviceChallengeStore>();
        services.AddSingleton<ILimitUsageStore, RedisLimitUsageStore>();

        // ---- Security and messaging ----
        services.AddSingleton<IMpinHasher, Argon2MpinHasher>();
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<ISmsSender, LoggingSmsSender>();
        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IBlindIndex, HmacBlindIndex>();
        services.AddSingleton<INadraClient, SimulatedNadraClient>();
        services.TryAddSingleton(TimeProvider.System);

        // ---- Use cases ----
        services.AddScoped<OtpService>();
        services.AddScoped<StartRegistrationHandler>();
        services.AddScoped<VerifyRegistrationOtpHandler>();
        services.AddScoped<CompleteRegistrationHandler>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<VerifyDeviceHandler>();
        services.AddScoped<RefreshSessionHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<GetProfileHandler>();
        services.AddScoped<ListDevicesHandler>();
        services.AddScoped<RevokeDeviceHandler>();
        services.AddScoped<SubmitKycHandler>();
        services.AddScoped<GetKycStatusHandler>();
        services.AddScoped<LimitService>();

        services.AddIdentityTokenValidation();

        // ---- Readiness: the service is ready only when its database and Redis are reachable ----
        services.AddHealthChecks()
            .AddDbContextCheck<IdentityDbContext>("postgres", tags: [HealthCheckTags.Ready])
            .AddCheck<RedisHealthCheck>("redis", tags: [HealthCheckTags.Ready]);

        return services;
    }

    /// <summary>
    /// The Identity service checks its own access tokens on /me and /devices (defence in depth: the
    /// gateway has already checked them). It uses its own public key directly, so no discovery call.
    /// </summary>
    private static void AddIdentityTokenValidation(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyProvider, JwtSigningOptions>((options, keys, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = WalletPkClaimTypes.Subject,
                    RoleClaimType = WalletPkClaimTypes.Role,
                };
            });

        services.AddAuthorization();
    }

    private static string RequiredConnectionString(IServiceProvider provider, string name)
    {
        var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"Connection string '{name}' is not configured.")
            : connectionString;
    }
}
