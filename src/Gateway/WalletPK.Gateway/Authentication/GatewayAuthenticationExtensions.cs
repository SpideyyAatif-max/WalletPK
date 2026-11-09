using WalletPK.Web.Authentication;

namespace WalletPK.Gateway.Authentication;

public static class GatewayAuthenticationExtensions
{
    /// <summary>
    /// Validates JWT access tokens at the edge (shared WalletPK settings, see
    /// <see cref="WalletPkJwtBearerExtensions.AddWalletPkJwtBearer"/>). Invalid or missing tokens on
    /// protected routes get 401 before any service is called.
    /// </summary>
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services)
    {
        services.AddWalletPkJwtBearer();

        services.AddAuthorizationBuilder()
            .AddPolicy(GatewayPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(GatewayPolicies.Operations, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(GatewayPolicies.RoleClaim, GatewayPolicies.OperationsRoles));

        return services;
    }
}
