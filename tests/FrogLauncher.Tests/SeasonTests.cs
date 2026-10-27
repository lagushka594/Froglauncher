using FrogLauncher.Services;

namespace FrogLauncher.Tests;

public class SeasonTests
{
    [Theory]
    [InlineData(2026, 3, 1, Season.Spring)]
    [InlineData(2026, 5, 31, Season.Spring)]
    [InlineData(2026, 6, 1, Season.Summer)]
    [InlineData(2026, 8, 31, Season.Summer)]
    [InlineData(2026, 9, 1, Season.Autumn)]
    [InlineData(2026, 11, 30, Season.Autumn)]
    [InlineData(2026, 12, 19, Season.Winter)]
    [InlineData(2026, 12, 20, Season.NewYear)]
    [InlineData(2027, 1, 10, Season.NewYear)]
    [InlineData(2027, 1, 11, Season.Winter)]
    [InlineData(2027, 2, 28, Season.Winter)]
    public void Season_by_date(int y, int m, int d, Season expected) => Assert.Equal(expected, Seasons.For(new DateOnly(y, m, d)));

    [Fact]
    public void Auto_theme_resolves_to_current_season()
    {
        Assert.Equal("season-newyear", ThemeService.Resolve(Seasons.AutoKey, new DateOnly(2026, 12, 31)).Key);
        Assert.Equal("season-spring", ThemeService.Resolve(Seasons.AutoKey, new DateOnly(2026, 4, 1)).Key);
        Assert.Equal("swamp", ThemeService.Resolve("swamp", new DateOnly(2026, 4, 1)).Key);
        Assert.All(new[] { Season.Spring, Season.Summer, Season.Autumn, Season.Winter, Season.NewYear },
            s => Assert.Equal(s, Seasons.FromKey(Seasons.KeyOf(s))));
    }

    [Fact]
    public void Winter_theme_in_july_triggers_easter_egg()
    {
        Assert.True(Seasons.IsWinterInSummer("season-winter", new DateOnly(2026, 7, 14)));
        Assert.False(Seasons.IsWinterInSummer("season-winter", new DateOnly(2026, 1, 14)));
        Assert.False(Seasons.IsWinterInSummer("season-summer", new DateOnly(2026, 7, 14)));
    }

    [Theory]
    [InlineData("2026-12-31T23:58:59", false)]
    [InlineData("2026-12-31T23:59:00", true)]
    [InlineData("2027-01-01T00:00:59", true)]
    [InlineData("2027-01-01T00:01:00", false)]
    public void Fireworks_window(string at, bool expected) =>
        Assert.Equal(expected, NewYearService.IsFireworksTime(DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
}

public class NewYearTests
{
    private static NewYearService Fresh() => new(new NewYearState());

    [Fact]
    public void Gift_once_per_day_grows_and_caps()
    {
        var ny = Fresh();
        var day = new DateOnly(2026, 12, 20);
        Assert.Equal(NewYearService.FirstGift, ny.ClaimGift(day));
        Assert.Equal(NewYearService.FirstGift, ny.ClaimGift(day)); // второй раз в тот же день — без изменений
        Assert.False(ny.CanClaim(day));
        for (var i = 1; i <= 20; i++) ny.ClaimGift(day.AddDays(i));
        Assert.Equal(NewYearService.MaxCoupon, ny.State.CouponPercent);
    }

    [Fact]
    public void Gifts_only_in_holidays_and_coupon_expires_after_season()
    {
        var ny = Fresh();
        Assert.Equal(0, ny.ClaimGift(new DateOnly(2026, 11, 30)));
        ny.ClaimGift(new DateOnly(2027, 1, 5));
        Assert.Equal(NewYearService.FirstGift, ny.ActiveCoupon(new DateOnly(2027, 1, 10)));
        Assert.Equal(0, ny.ActiveCoupon(new DateOnly(2027, 1, 11)));
        // Следующие праздники начинаются с чистого листа.
        Assert.Equal(0, ny.ActiveCoupon(new DateOnly(2027, 12, 25)));
        Assert.Equal(NewYearService.FirstGift, ny.ClaimGift(new DateOnly(2027, 12, 25)));
    }

    [Fact]
    public void Tree_decorations_by_login_days()
    {
        var ny = Fresh();
        var start = new DateOnly(2026, 12, 20);
        for (var i = 0; i < 4; i++) ny.RecordLogin(start.AddDays(i));
        ny.RecordLogin(start.AddDays(3)); // повторный вход в тот же день не считается
        Assert.Equal(4, ny.Days(start.AddDays(3)));
        Assert.False(NewYearService.HasGarland(4));
        ny.RecordLogin(start.AddDays(4));
        Assert.True(NewYearService.HasGarland(ny.Days(start.AddDays(4))));
        Assert.False(NewYearService.HasStar(5));
        Assert.True(NewYearService.HasStar(10));
        Assert.Equal(2026, NewYearService.SeasonYearOf(new DateOnly(2027, 1, 3)));
    }
}

public class SoundTests
{
    [Theory]
    [InlineData(Season.Spring)]
    [InlineData(Season.Summer)]
    [InlineData(Season.Autumn)]
    [InlineData(Season.Winter)]
    [InlineData(Season.NewYear)]
    public void Every_season_has_a_short_audible_sound(Season s)
    {
        var samples = SoundService.Synthesize(s);
        Assert.InRange(samples.Length / (double)SoundService.Rate, 0.5, 5);
        Assert.True(samples.Max(Math.Abs) > 0.05f);
        var wav = SoundService.Wav(samples);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(44 + samples.Length * 2, wav.Length);
    }

    [Fact]
    public void Notes_are_tuned() => Assert.Equal(440, SoundService.Note("A4"), 3);
}
