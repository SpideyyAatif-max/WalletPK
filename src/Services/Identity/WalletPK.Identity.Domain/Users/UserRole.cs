namespace WalletPK.Identity.Domain.Users;

/// <summary>What kind of account this is; travels in the access token's "role" claim.</summary>
public enum UserRole
{
    Customer = 0,
    Merchant = 1,
    Agent = 2,
    Ops = 3,
    Compliance = 4,
    Finance = 5,
    Admin = 6,
}

public static class UserRoleExtensions
{
    /// <summary>The value used in the "role" claim, matching the gateway's policies ("customer", "ops", ...).</summary>
    public static string ToClaimValue(this UserRole role) => role switch
    {
        UserRole.Customer => "customer",
        UserRole.Merchant => "merchant",
        UserRole.Agent => "agent",
        UserRole.Ops => "ops",
        UserRole.Compliance => "compliance",
        UserRole.Finance => "finance",
        UserRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };
}
