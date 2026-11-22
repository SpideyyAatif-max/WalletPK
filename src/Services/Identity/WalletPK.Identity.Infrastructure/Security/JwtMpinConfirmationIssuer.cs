using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Web.Identity;
using WalletPK.Web.Security;

namespace WalletPK.Identity.Infrastructure.Security;

/// <summary>
/// Signs MPIN confirmations with the access-token key, so every service checks them with the keys it already
/// has. Audience "walletpk-confirmation": never valid as an access token, nor an access token as one.
/// </summary>
internal sealed class JwtMpinConfirmationIssuer(SigningKeyProvider keys, JwtSigningOptions jwt) : IMpinConfirmationIssuer
{
    /// <summary>Time to send the payment (and, if the fraud engine asks, to confirm it with a code too).</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(5);

    private readonly JsonWebTokenHandler _handler = new();

    public IssuedMpinConfirmation Issue(UserId userId, DeviceId deviceId, string binding, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        var expiresAt = now + Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = MpinConfirmation.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
                [WalletPkClaimTypes.DeviceId] = deviceId.ToString(),
                [MpinConfirmation.BindingClaim] = binding,
            },
            SigningCredentials = keys.SigningCredentials,
        });
        return new IssuedMpinConfirmation(token, expiresAt);
    }
}
