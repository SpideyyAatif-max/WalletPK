namespace WalletPK.Gateway.RateLimiting;

/// <summary>
/// Rate limits, from the "RateLimiting" configuration section. Each request must pass every
/// applicable limit: per IP, per signed-in user and per device. Login and OTP routes have an
/// extra strict limit against brute force. The simulator environment raises these.
/// </summary>
public sealed class GatewayRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int PerIpTokenLimit { get; set; } = 200;

    public int PerIpTokensPerSecond { get; set; } = 100;

    public int PerUserTokenLimit { get; set; } = 60;

    public int PerUserTokensPerSecond { get; set; } = 20;

    public int PerDeviceTokenLimit { get; set; } = 60;

    public int PerDeviceTokensPerSecond { get; set; } = 20;

    /// <summary>Requests per window to login, sign-up, OTP and token refresh, per IP.</summary>
    public int AuthPermitLimit { get; set; } = 10;

    public int AuthWindowSeconds { get; set; } = 60;
}
