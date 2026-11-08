using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using WalletPK.Web.Correlation;

namespace WalletPK.Gateway.Cors;

/// <summary>Allowed browser origins, from the "Cors" configuration section.</summary>
public sealed class GatewayCorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>For example "http://localhost:3000" for the Next.js app in development.</summary>
    public List<string> AllowedOrigins { get; } = [];
}

public static class GatewayCorsExtensions
{
    public const string PolicyName = "walletpk-web";

    /// <summary>
    /// Lets the WalletPK web app (on its own origin) call the API from the browser, including
    /// SignalR, which needs credentials. Only configured origins are allowed, never "*".
    /// </summary>
    public static IServiceCollection AddGatewayCors(this IServiceCollection services)
    {
        services.AddOptions<GatewayCorsOptions>().BindConfiguration(GatewayCorsOptions.SectionName);
        services.AddCors();

        services.AddOptions<CorsOptions>()
            .Configure<IOptions<GatewayCorsOptions>>((cors, gatewayCors) =>
                cors.AddPolicy(PolicyName, policy => policy
                    .WithOrigins([.. gatewayCors.Value.AllowedOrigins])
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "Retry-After")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }
}
