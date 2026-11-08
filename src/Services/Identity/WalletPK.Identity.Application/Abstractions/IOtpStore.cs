using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>Why an OTP was sent; each purpose has its own code, so codes cannot be reused across flows.</summary>
public enum OtpPurpose
{
    Registration = 0,
    DeviceVerification = 1,
}

/// <summary>An outstanding one-time password: only its keyed hash is stored, never the code.</summary>
public sealed record OtpRecord(string CodeHash, int FailedAttempts);

/// <summary>Short-lived OTP storage with automatic expiry (Redis in production).</summary>
public interface IOtpStore
{
    /// <summary>Starts the resend cooldown. Returns null if it started, otherwise the time left on the running cooldown.</summary>
    Task<TimeSpan?> TryBeginCooldownAsync(OtpPurpose purpose, PhoneNumber phoneNumber, TimeSpan cooldown, CancellationToken cancellationToken);

    /// <summary>Saves a new code hash (replacing any previous one) that expires after <paramref name="timeToLive"/>.</summary>
    Task SaveAsync(OtpPurpose purpose, PhoneNumber phoneNumber, string codeHash, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task<OtpRecord?> GetAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken);

    /// <summary>Records a wrong guess and returns the total number of wrong guesses so far.</summary>
    Task<int> IncrementFailedAttemptsAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken);

    Task DeleteAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken);
}
