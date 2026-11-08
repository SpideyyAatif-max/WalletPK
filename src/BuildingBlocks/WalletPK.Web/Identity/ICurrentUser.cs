using WalletPK.Common.Identifiers;

namespace WalletPK.Web.Identity;

/// <summary>The authenticated caller of the current request, read from the access token.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The user's ID, or null when the request is anonymous.</summary>
    UserId? Id { get; }

    string? DeviceId { get; }

    string? KycTier { get; }

    bool IsInRole(string role);

    /// <summary>The user's ID; throws if the request is anonymous (use only on endpoints that require login).</summary>
    UserId GetRequiredId();
}
