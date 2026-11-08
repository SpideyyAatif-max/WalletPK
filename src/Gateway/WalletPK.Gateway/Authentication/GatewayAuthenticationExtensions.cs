using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Web.Identity;

namespace WalletPK.Gateway.Authentication;

public static class GatewayAuthenticationExtensions
{
    /// <summary>Path prefix of SignalR hubs, which send the token in the query string.</summary>
    private const string HubsPath = "/hubs";

    /// <summary>
    /// Validates JWT access tokens at the edge: signature (RS256/ES256 via the issuer's published keys),
    /// issuer, audience and expiry. Invalid or missing tokens on protected routes get 401 before
    /// any service is called.
    /// </summary>
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

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

        services.AddAuthorizationBuilder()
            .AddPolicy(GatewayPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(GatewayPolicies.Operations, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(GatewayPolicies.RoleClaim, GatewayPolicies.OperationsRoles));

        return services;
    }
}
