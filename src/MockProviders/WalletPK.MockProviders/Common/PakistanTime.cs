namespace WalletPK.MockProviders.Common;

/// <summary>Business dates are Pakistan dates (PKT, UTC+5, no daylight saving).</summary>
public static class PakistanTime
{
    private static readonly TimeSpan _offset = TimeSpan.FromHours(5);

    public static DateOnly BusinessDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(_offset).DateTime);

    public static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), _offset);
}
