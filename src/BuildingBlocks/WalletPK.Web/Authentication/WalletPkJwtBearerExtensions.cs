using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Web.Identity;

namespace WalletPK.Web.Authentication;

public static class WalletPkJwtBearerExtensions
{
    /// <summary>Path prefix of SignalR hubs, which send the token in the query string.</summary>
    private const string HubsPath = "/hubs";

    /// <summary>
    /// Validates WalletPK access tokens: signature (RS256/ES256 with the Identity service's published keys,
    /// discovered from <c>{Jwt:Authority}/.well-known/openid-configuration</c>), issuer, audience and expiry.
    /// Shared by the gateway (which checks every request at the edge) and each service (which checks again:
    /// defence in depth, so a service is never open just because a request reached it without the gateway).
    /// </summary>
    public static AuthenticationBuilder AddWalletPkJwtBearer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName);

        var authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured lazily from IOptions so settings from every configuration source are applied.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                options.Authority = jwt.Authority.ToString();
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;

                // Keep claim names exactly as issued ("sub", "role"), no legacy WS-* renaming.
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
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                    NameClaimType = WalletPkClaimTypes.Subject,
                    RoleClaimType = WalletPkClaimTypes.Role,
                };

                options.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on WebSocket connections, so SignalR sends ?access_token=.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"].ToString();
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(HubsPath))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        return authentication;
    }
}
