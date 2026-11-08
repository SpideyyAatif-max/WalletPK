using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>
/// Remembers, for a few minutes, that a phone number passed OTP verification, so the next step
/// (choosing an MPIN) can only be done by the person who verified it.
/// </summary>
public interface IRegistrationTokenStore
{
    Task SaveAsync(string token, PhoneNumber phoneNumber, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Returns the verified phone number and deletes the token (single use), or null if unknown or expired.</summary>
    Task<PhoneNumber?> ConsumeAsync(string token, CancellationToken cancellationToken);
}
