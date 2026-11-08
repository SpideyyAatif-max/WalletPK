namespace WalletPK.Identity.Application.Authentication;

/// <summary>Login, session and device settings, from the "Auth" configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Access tokens are short-lived so a stolen one is useful only briefly.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>A session stays alive this long without use; every refresh restarts the clock.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    public int MaxFailedMpinAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 30;

    /// <summary>Limited-trust period for a device added while the user already has another one.</summary>
    public int NewDeviceCoolingOffHours { get; set; } = 24;

    /// <summary>How long the user has to enter the OTP when logging in on a new device.</summary>
    public int DeviceChallengeSeconds { get; set; } = 300;

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(LockoutMinutes);

    public TimeSpan NewDeviceCoolingOff => TimeSpan.FromHours(NewDeviceCoolingOffHours);

    public TimeSpan DeviceChallengeLifetime => TimeSpan.FromSeconds(DeviceChallengeSeconds);
}
