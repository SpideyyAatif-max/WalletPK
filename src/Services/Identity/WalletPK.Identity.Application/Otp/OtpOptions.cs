namespace WalletPK.Identity.Application.Otp;

/// <summary>One-time password settings, from the "Otp" configuration section.</summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    public int Length { get; set; } = 6;

    public int TimeToLiveSeconds { get; set; } = 120;

    /// <summary>Wrong guesses allowed before the code is destroyed and a new one must be requested.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Minimum time between two codes for the same number and purpose.</summary>
    public int ResendCooldownSeconds { get; set; } = 30;

    /// <summary>
    /// Secret key mixed into every stored code hash, so a leaked Redis dump cannot be brute-forced
    /// (6-digit codes have only a million possibilities). At least 32 characters; never commit a real one.
    /// </summary>
    public string Pepper { get; set; } = string.Empty;
}
