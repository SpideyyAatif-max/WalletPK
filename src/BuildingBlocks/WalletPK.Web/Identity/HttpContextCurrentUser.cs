using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WalletPK.Common.Identifiers;

namespace WalletPK.Web.Identity;

/// <summary><see cref="ICurrentUser"/> backed by the claims of the current HTTP request.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public bool IsAuthenticated => TryGetAuthenticatedPrincipal(out _);

    public UserId? Id
    {
        get
        {
            if (!TryGetAuthenticatedPrincipal(out var principal))
            {
                return null;
            }

            // "sub" when inbound claim mapping is off (our default); NameIdentifier when it is on.
            var value = principal.FindFirstValue(WalletPkClaimTypes.Subject)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

            return UserId.TryParse(value, null, out var id) ? id : null;
        }
    }

    public string? DeviceId =>
        TryGetAuthenticatedPrincipal(out var principal) ? principal.FindFirstValue(WalletPkClaimTypes.DeviceId) : null;

    public string? KycTier =>
        TryGetAuthenticatedPrincipal(out var principal) ? principal.FindFirstValue(WalletPkClaimTypes.KycTier) : null;

    public string? PhoneNumber =>
        TryGetAuthenticatedPrincipal(out var principal) ? principal.FindFirstValue(WalletPkClaimTypes.PhoneNumber) : null;

    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        return TryGetAuthenticatedPrincipal(out var principal) &&
            (principal.HasClaim(WalletPkClaimTypes.Role, role) || principal.IsInRole(role));
    }

    public UserId GetRequiredId() =>
        Id ?? throw new InvalidOperationException("This operation requires an authenticated user with a valid user ID.");

    /// <summary>Reads the request's user once; true only when it is authenticated.</summary>
    private bool TryGetAuthenticatedPrincipal([NotNullWhen(true)] out ClaimsPrincipal? principal)
    {
        principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        principal = null;
        return false;
    }
}
