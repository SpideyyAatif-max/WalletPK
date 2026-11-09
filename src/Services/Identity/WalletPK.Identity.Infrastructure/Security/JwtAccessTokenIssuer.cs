using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Authentication;
using WalletPK.Identity.Domain.Users;
using WalletPK.Web.Identity;

namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// Issues RS256-signed JWT access tokens, for example:
/// <code>
/// { "iss": "walletpk-identity", "aud": "walletpk-api", "sub": "0193…", "device_id": "0193…",
///   "kyc_tier": "L0", "role": "customer", "phone_number": "+923001234567", "jti": "…", "iat": …, "nbf": …, "exp": … }
/// </code>
/// </summary>
internal sealed class JwtAccessTokenIssuer(SigningKeyProvider keys, JwtSigningOptions jwt, AuthOptions auth) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedAccessToken Issue(AccessTokenClaims claims, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var expiresAt = now + auth.AccessTokenLifetime;
        var payload = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = claims.UserId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
            [WalletPkClaimTypes.DeviceId] = claims.DeviceId.ToString(),
            [WalletPkClaimTypes.KycTier] = claims.KycTier.ToString(),
            [WalletPkClaimTypes.Role] = claims.Role.ToClaimValue(),
        };

        if (claims.PhoneNumber is { Length: > 0 } phoneNumber)
        {
            payload[WalletPkClaimTypes.PhoneNumber] = phoneNumber;
        }

        if (claims.CoolingOffUntil is { } coolingOffUntil)
        {
            payload[WalletPkClaimTypes.CoolingOffUntil] = coolingOffUntil.ToUnixTimeSeconds();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = payload,
            SigningCredentials = keys.SigningCredentials,
        };

        return new IssuedAccessToken(_handler.CreateToken(descriptor), expiresAt);
    }
}
