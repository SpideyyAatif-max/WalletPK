using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Otp;
using WalletPK.Identity.Application.Registration;
using WalletPK.Identity.Infrastructure.Health;
using WalletPK.Identity.Infrastructure.Persistence;
using WalletPK.Identity.Infrastructure.Redis;
using WalletPK.Identity.Infrastructure.Security;
using WalletPK.Identity.Infrastructure.Sms;
using WalletPK.Web.Health;

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

        // Application classes take plain option objects (they do not depend on Microsoft.Extensions.Options).
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<OtpOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MpinHashingOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<RegistrationOptions>>().Value);

        // ---- PostgreSQL (EF Core) ----
        services.AddDbContext<IdentityDbContext>((provider, options) => options
            .UseNpgsql(
                RequiredConnectionString(provider, DatabaseConnectionName),
                npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();

        // ---- Redis ----
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            ConnectionMultiplexer.Connect(RequiredConnectionString(provider, RedisConnectionName)));
        services.AddSingleton<IOtpStore, RedisOtpStore>();
        services.AddSingleton<IRegistrationTokenStore, RedisRegistrationTokenStore>();

        // ---- Security and messaging ----
        services.AddSingleton<IMpinHasher, Argon2MpinHasher>();
        services.AddSingleton<ISmsSender, LoggingSmsSender>();
        services.TryAddSingleton(TimeProvider.System);

        // ---- Use cases ----
        services.AddScoped<OtpService>();
        services.AddScoped<StartRegistrationHandler>();
        services.AddScoped<VerifyRegistrationOtpHandler>();
        services.AddScoped<CompleteRegistrationHandler>();

        // ---- Readiness: the service is ready only when its database and Redis are reachable ----
        services.AddHealthChecks()
            .AddDbContextCheck<IdentityDbContext>("postgres", tags: [HealthCheckTags.Ready])
            .AddCheck<RedisHealthCheck>("redis", tags: [HealthCheckTags.Ready]);

        return services;
    }

    private static string RequiredConnectionString(IServiceProvider provider, string name)
    {
        var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"Connection string '{name}' is not configured.")
            : connectionString;
    }
}
