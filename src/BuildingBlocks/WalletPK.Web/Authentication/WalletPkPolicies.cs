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

    /// <summary>Compliance officers (fraud cases, freezes, rules) and admins.</summary>
    public const string Compliance = "compliance";

    public static IReadOnlyList<string> ComplianceRoles { get; } = ["compliance", "admin"];

    /// <summary>Finance (reconciliation runs and breaks, M12) and admins.</summary>
    public const string Finance = "finance";

    public static IReadOnlyList<string> FinanceRoles { get; } = ["finance", "admin"];

    /// <summary>Adds <see cref="Authenticated"/>, <see cref="Operations"/>, <see cref="Compliance"/> and <see cref="Finance"/>.</summary>
    public static AuthorizationBuilder AddWalletPkPolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Operations, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(WalletPkClaimTypes.Role, OperationsRoles))
            .AddPolicy(Compliance, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(WalletPkClaimTypes.Role, ComplianceRoles))
            .AddPolicy(Finance, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(WalletPkClaimTypes.Role, FinanceRoles));
    }
}
