using RibbixLauncher.Services;

namespace RibbixLauncher.Tests;

public class V31Tests
{
    private static readonly DateTime Day = new(2026, 10, 29, 0, 0, 0);

    [Fact]
    public void Weeks_start_on_monday()
    {
        var thu = new DateOnly(2026, 10, 29);
        Assert.Equal(new DateOnly(2026, 10, 26), QuestService.WeekStart(thu));
        Assert.Equal(new DateOnly(2026, 10, 26), QuestService.WeekStart(new DateOnly(2026, 10, 26)));
        Assert.Equal(new DateOnly(2026, 10, 26), QuestService.WeekStart(new DateOnly(2026, 11, 1)));
        Assert.Equal(QuestService.WeekKey(thu), QuestService.WeekKey(new DateOnly(2026, 11, 1)));
        Assert.NotEqual(QuestService.WeekKey(thu), QuestService.WeekKey(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void Pass_rewards_have_themes()
    {
        foreach (var (level, _) in QuestService.Rewards)
            Assert.Contains(ThemeService.Themes, t => t.Requires == ThemeService.QuestPrefix + level);
    }

    [Fact]
    public void Similar_mods_use_genres_not_loaders()
    {
        var genres = ModrinthService.TopGenres(new[]
        {
            new[] { "fabric", "technology", "storage" },
            new[] { "forge", "fabric", "technology" },
            new[] { "library", "fabric", "magic" },
        }, 2);
        Assert.Equal(new[] { "technology", "magic" }, genres);
    }

    [Fact]
    public void Achievements_unique_and_testable()
    {
        Assert.Equal(AchievementService.All.Count, AchievementService.All.Select(a => a.Id).Distinct().Count());
        var none = new AchievementContext(0, 0, 0, 0, 0, 0, 0, 1, 0, new Dictionary<string, int>());
        Assert.DoesNotContain(AchievementService.All, a => a.Test(none));
        var host = none with { Marks = new Dictionary<string, int> { ["host"] = 1 }, Sessions = 1 };
        Assert.Contains(AchievementService.All, a => a.Id == "host" && a.Test(host));
        Assert.Contains(AchievementService.All, a => a.Id == "first" && a.Test(host));
    }

    private static (RibbixLauncher.Models.InstalledMod, ISet<string>) Mod(string project, string version, string title, params string[] incompatible) =>
        (new RibbixLauncher.Models.InstalledMod { ProjectId = project, VersionId = version, Title = title }, new HashSet<string>(incompatible));

    [Fact]
    public void Conflicts_both_directions_and_render_engines()
    {
        var installed = new[] { Mod("AAA", "v1", "Alpha"), Mod("BBB", "v2", "Beta", "NEW"), Mod("CCC", "v3", "Gamma") };
        // Новый объявляет Alpha несовместимым, Beta объявляет несовместимым новый; Gamma ни при чём.
        var found = ModrinthService.FindConflicts("NEW", "newmod", new HashSet<string> { "AAA" }, installed, Array.Empty<string>());
        Assert.Equal(new[] { "Alpha", "Beta" }, found);
        // Несовместимость по ID версии тоже считается.
        Assert.Equal(new[] { "Gamma" }, ModrinthService.FindConflicts("NEW", "newmod", new HashSet<string> { "v3" },
            new[] { Mod("CCC", "v3", "Gamma") }, Array.Empty<string>()));
        // Второй движок отрисовки: Sodium рядом с OptiFine — конфликт, рядом с Sodium Extra — нет.
        Assert.Equal(new[] { "OptiFine" }, ModrinthService.FindConflicts("X", "sodium", new HashSet<string>(),
            Array.Empty<(RibbixLauncher.Models.InstalledMod, ISet<string>)>(), new[] { "OptiFine_1.20.1_HD_U_I6.jar", "sodium-extra-0.5.jar" }));
        Assert.Empty(ModrinthService.FindConflicts("X", "lithium", new HashSet<string>(),
            Array.Empty<(RibbixLauncher.Models.InstalledMod, ISet<string>)>(), new[] { "OptiFine_1.20.1.jar" }));
    }

    [Fact]
    public void Incompatible_reads_modrinth_dependencies()
    {
        var v = System.Text.Json.Nodes.JsonNode.Parse("""
            {"dependencies":[{"project_id":"P1","dependency_type":"incompatible"},
                             {"project_id":"P2","dependency_type":"required"},
                             {"project_id":null,"version_id":"V9","dependency_type":"incompatible"}]}
            """);
        Assert.Equal(new HashSet<string> { "P1", "V9" }, ModrinthService.Incompatible(v));
    }

    [Fact]
    public void Launch_estimate_time_and_disk()
    {
        var e = new LaunchEstimate(600L << 20, 750L << 20, "C:", 10L << 30, 0);
        Assert.True(e.Worth);
        Assert.False(e.LowDisk);
        Assert.Equal(200, e.Time.TotalSeconds, 0); // 600 МБ при 3 МБ/с по умолчанию
        Assert.Equal(60, (e with { BytesPerSecond = 10 << 20 }).Time.TotalSeconds, 0);
        Assert.True((e with { FreeBytes = 1L << 30 }).LowDisk);
        Assert.False((e with { FreeBytes = -1 }).LowDisk); // место неизвестно — не пугаем
        Assert.False(new LaunchEstimate(5L << 20, 5L << 20, "C:", 1L << 40, 0).Worth);
    }

    [Fact]
    public void Clipboard_invites_offered_once_and_not_own()
    {
        Assert.Null(InviteClipboard.Take("просто текст"));
        Assert.Null(InviteClipboard.Take("https://example.com/ribbix://play?mc=1.21"));
        var link = "ribbix://play?n=Test&mc=1.21.1&l=fabric&s=abc.e4mc.link";
        Assert.NotNull(InviteClipboard.Take(link));
        Assert.Null(InviteClipboard.Take(link)); // второй раз не предлагаем
        Assert.NotNull(InviteClipboard.Take("ribbix://pack?gist=0123456789abcdef0123"));
        var invite = FriendLinkService.Parse(new Uri(link))!;
        Assert.Equal("abc.e4mc.link", invite.Server);
    }
}
