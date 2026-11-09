using Microsoft.AspNetCore.Authorization;
using WalletPK.Web.Identity;

namespace WalletPK.Web.Authentication;

/// <summary>Authorization policies shared by the gateway (YARP routes) and every service's endpoints.</summary>
public static class WalletPkPolicies
{
    /// <summary>Any signed-in user (customer, merchant, agent or staff).</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Back-office staff only.</summary>
    public const string Operations = "operations";

    public static IReadOnlyList<string> OperationsRoles { get; } = ["ops", "compliance", "finance", "admin"];

    /// <summary>Adds <see cref="Authenticated"/> and <see cref="Operations"/>.</summary>
    public static AuthorizationBuilder AddWalletPkPolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Operations, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(WalletPkClaimTypes.Role, OperationsRoles));
    }
}
