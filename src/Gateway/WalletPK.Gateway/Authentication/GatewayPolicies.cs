using WalletPK.Web.Identity;

namespace WalletPK.Gateway.Authentication;

/// <summary>Authorization policy names referenced by YARP routes in appsettings.json.</summary>
public static class GatewayPolicies
{
    /// <summary>Any signed-in user (customer, merchant, agent or staff).</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Back-office staff only.</summary>
    public const string Operations = "operations";

    public static IReadOnlyList<string> OperationsRoles { get; } = ["ops", "compliance", "finance", "admin"];

    internal static string RoleClaim => WalletPkClaimTypes.Role;
}
