using WalletPK.Common.Time;

namespace WalletPK.Common.UnitTests.Time;

public class PakistanTimeTests
{
    [Fact]
    public void Late_evening_utc_is_already_the_next_business_day_in_pakistan()
    {
        // 20:30 UTC on 8 Nov = 01:30 on 9 Nov in Pakistan
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 8, 20, 30, 0, TimeSpan.Zero));

        clock.GetBusinessDate().ShouldBe(new DateOnly(2026, 11, 9));
        clock.GetPakistanNow().Hour.ShouldBe(1);
        clock.GetPakistanNow().Offset.ShouldBe(TimeSpan.FromHours(5));
    }

    [Fact]
    public void ToBusinessDate_converts_any_timestamp()
    {
        var utcMorning = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

        utcMorning.ToBusinessDate().ShouldBe(new DateOnly(2026, 11, 8));
        utcMorning.ToPakistanTime().Hour.ShouldBe(8);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
