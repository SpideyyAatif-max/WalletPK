using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>
/// A login that passed the MPIN check from an unknown device and is waiting for the OTP.
/// Kept for a few minutes only; the device is trusted once the OTP is verified.
/// </summary>
public sealed record DeviceChallenge(UserId UserId, string InstallationId, string DeviceName);

/// <summary>Short-lived storage for device challenges (Redis in production).</summary>
public interface IDeviceChallengeStore
{
    Task SaveAsync(string challengeId, DeviceChallenge challenge, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task<DeviceChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken);

    Task DeleteAsync(string challengeId, CancellationToken cancellationToken);
}
