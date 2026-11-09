using WalletPK.Common.Identifiers;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>What an access token says about its holder.</summary>
/// <param name="UserId">Becomes the "sub" claim.</param>
/// <param name="DeviceId">Becomes the "device_id" claim.</param>
/// <param name="KycTier">Becomes the "kyc_tier" claim; other services use it for limits.</param>
/// <param name="Role">Becomes the "role" claim.</param>
/// <param name="CoolingOffUntil">Set while the device is in its new-device cooling-off period.</param>
/// <param name="PhoneNumber">Becomes the "phone_number" claim (E.164); the Ledger uses it as the wallet number.</param>
public sealed record AccessTokenClaims(UserId UserId, DeviceId DeviceId, KycTier KycTier, UserRole Role, DateTimeOffset? CoolingOffUntil, string? PhoneNumber = null);

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Creates signed, short-lived access tokens (RS256 JWTs in production).</summary>
public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenClaims claims, DateTimeOffset now);
}
