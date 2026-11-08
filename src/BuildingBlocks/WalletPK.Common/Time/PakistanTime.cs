namespace WalletPK.Common.Time;

/// <summary>
/// Pakistan Standard Time helpers. Timestamps are always stored in UTC; Pakistan time
/// (UTC+05:00, no daylight saving) is used only for business dates such as
/// "daily limit resets at midnight" and end-of-day reconciliation.
/// </summary>
/// <remarks>
/// Time is read through <see cref="TimeProvider"/> (built into .NET), never DateTime.Now,
/// so tests can control the clock.
/// </remarks>
public static class PakistanTime
{
    /// <summary>Pakistan's fixed offset from UTC.</summary>
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(5);

    /// <summary>The current time in Pakistan.</summary>
    public static DateTimeOffset GetPakistanNow(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return timeProvider.GetUtcNow().ToOffset(UtcOffset);
    }

    /// <summary>Today's business date in Pakistan.</summary>
    public static DateOnly GetBusinessDate(this TimeProvider timeProvider) =>
        DateOnly.FromDateTime(timeProvider.GetPakistanNow().DateTime);

    /// <summary>Converts any timestamp to Pakistan time.</summary>
    public static DateTimeOffset ToPakistanTime(this DateTimeOffset value) => value.ToOffset(UtcOffset);

    /// <summary>The Pakistan business date a timestamp falls on.</summary>
    public static DateOnly ToBusinessDate(this DateTimeOffset value) =>
        DateOnly.FromDateTime(value.ToOffset(UtcOffset).DateTime);
}
