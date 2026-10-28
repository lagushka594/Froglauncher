using System.IO;
using System.Windows.Media;
using RibbixLauncher.Services;

namespace RibbixLauncher.Tests;

public class YearRecapTests
{
    private static PlaySession S(string id, string start, double hours, string? server = null)
    {
        var t = new DateTimeOffset(DateTime.Parse(start)); // местное время — как в playtime.json
        return new PlaySession { InstanceId = id, InstanceName = id, Start = t, End = t.AddHours(hours), Server = server };
    }

    internal static readonly McStats Mc = new(20 * 3600 * 200, 54_321, 9_870_000_00, 77, 1234, 87, 5);

    internal static YearRecap Sample() => YearRecapService.Compute(2026, new[]
    {
        S("wiz", "2025-12-31T20:00:00", 3),          // прошлый год — не считается
        S("wiz", "2026-03-01T21:00:00", 2),
        S("wiz", "2026-03-02T23:00:00", 4, "mc.hypixel.net"),
        S("wiz", "2026-03-03T22:30:00", 1),
        S("opt", "2026-07-10T14:00:00", 1.5),
    }, new Dictionary<string, string> { ["wiz"] = "Wizards RPG" },
        new[] { new DateTime(2026, 3, 2), new DateTime(2025, 1, 1) }, Mc, 87);

    [Fact]
    public void Recap_counts_only_this_year()
    {
        var r = Sample();
        Assert.Equal(4, r.Sessions);
        Assert.Equal(TimeSpan.FromHours(8.5), r.Total);
        Assert.Equal("Wizards RPG", r.Favorite!.Name);
        Assert.Equal(7.0 / 8.5, r.Favorite.Share, 3);
        Assert.Equal(2, r.VersionsPlayed);
        Assert.Equal(1, r.Screenshots);
        Assert.Equal("mc.hypixel.net", r.FavoriteServer);
        Assert.Equal(2, r.BestMonth); // март
        Assert.Equal(TimeSpan.FromHours(4), r.Longest);
    }

    [Fact]
    public void Streak_and_best_day_split_sessions_at_midnight()
    {
        var r = Sample();
        // 1–3 марта подряд; сессия 2-го уходит за полночь, и 3 из 4 её часов достаются 3-му.
        Assert.Equal(3, r.BestStreak);
        Assert.Equal(new DateOnly(2026, 3, 3), r.BestDay);
        Assert.Equal(TimeSpan.FromHours(4), r.BestDayTime);
        Assert.Equal("night", r.Rhythm);
    }

    [Fact]
    public void Empty_year_gives_single_card()
    {
        var r = YearRecapService.Compute(2026, Array.Empty<PlaySession>(), new Dictionary<string, string>(), Array.Empty<DateTime>(), Mc, 0);
        Assert.True(r.IsEmpty);
        Assert.Equal(1, YearRecapService.CardCount(r));
    }

    [Fact]
    public void Every_card_renders()
    {
        var dump = Environment.GetEnvironmentVariable("RIBBIX_RECAP_DUMP");
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var r = Sample();
                for (var i = 0; i < YearRecapService.CardCount(r); i++)
                {
                    var card = YearRecapService.Render(r, i, "Lagushechka", Color.FromRgb(0x22, 0xC5, 0x5E));
                    Assert.Equal(YearRecapService.Width, card.PixelWidth);
                    if (!string.IsNullOrEmpty(dump)) YearRecapService.Save(card, Path.Combine(dump, $"card{i + 1}.png"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
