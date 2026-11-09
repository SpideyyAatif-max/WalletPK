using WalletPK.Web.Authentication;

namespace WalletPK.Gateway.Authentication;

/// <summary>Authorization policy names referenced by YARP routes in appsettings.json (shared with the services).</summary>
public static class GatewayPolicies
{
    /// <summary>Any signed-in user (customer, merchant, agent or staff).</summary>
    public const string Authenticated = WalletPkPolicies.Authenticated;

    /// <summary>Back-office staff only.</summary>
    public const string Operations = WalletPkPolicies.Operations;

    public static IReadOnlyList<string> OperationsRoles => WalletPkPolicies.OperationsRoles;
}
