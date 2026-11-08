using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Sessions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Authentication;

/// <summary>
/// Creates the access token + refresh token pair for a user on a trusted device. The new refresh
/// token is added to the repository; the calling use case saves it in its own transaction.
/// </summary>
public sealed class SessionIssuer(IAccessTokenIssuer accessTokens, IRefreshTokenRepository refreshTokens, AuthOptions options)
{
    /// <summary>A fresh login: starts a new refresh-token family.</summary>
    public AuthTokens Start(User user, Device device, DateTimeOffset now) =>
        Issue(user, device, Guid.CreateVersion7(), now);

    /// <summary>A refresh: the new token joins the existing family, so reuse of any old one can revoke them all.</summary>
    public AuthTokens Continue(User user, Device device, Guid familyId, DateTimeOffset now) =>
        Issue(user, device, familyId, now);

    private AuthTokens Issue(User user, Device device, Guid familyId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(device);

        var coolingOffUntil = device.ActiveCoolingOffUntil(now);
        var access = accessTokens.Issue(new AccessTokenClaims(user.Id, device.Id, user.KycTier, user.Role, coolingOffUntil), now);

        var secret = RefreshTokenSecret.Generate();
        refreshTokens.Add(RefreshToken.Issue(user.Id, device.Id, familyId, RefreshTokenSecret.Hash(secret), now, options.RefreshTokenLifetime));

        return new AuthTokens(
            access.Token,
            "Bearer",
            (int)(access.ExpiresAt - now).TotalSeconds,
            secret,
            (int)options.RefreshTokenLifetime.TotalSeconds,
            user.Id,
            device.Id,
            coolingOffUntil);
    }
}
