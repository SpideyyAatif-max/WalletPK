using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WalletPK.Common.Identifiers;

namespace WalletPK.Web.Identity;

/// <summary><see cref="ICurrentUser"/> backed by the claims of the current HTTP request.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public UserId? Id
    {
        get
        {
            if (!IsAuthenticated)
            {
                return null;
            }

            // "sub" when inbound claim mapping is off (our default); NameIdentifier when it is on.
            var value = Principal!.FindFirstValue(WalletPkClaimTypes.Subject)
                ?? Principal.FindFirstValue(ClaimTypes.NameIdentifier);

            return UserId.TryParse(value, null, out var id) ? id : null;
        }
    }

    public string? DeviceId => IsAuthenticated ? Principal!.FindFirstValue(WalletPkClaimTypes.DeviceId) : null;

    public string? KycTier => IsAuthenticated ? Principal!.FindFirstValue(WalletPkClaimTypes.KycTier) : null;

    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return IsAuthenticated &&
            (Principal!.HasClaim(WalletPkClaimTypes.Role, role) || Principal.IsInRole(role));
    }

    public UserId GetRequiredId() =>
        Id ?? throw new InvalidOperationException("This operation requires an authenticated user with a valid user ID.");
}
