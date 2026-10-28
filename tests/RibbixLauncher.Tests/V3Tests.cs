using RibbixLauncher.Models;
using RibbixLauncher.Services;

namespace RibbixLauncher.Tests;

public class V3Tests
{
    private static ModInfo Mod(string jar, string id) => new(jar, true, new[] { id }, id, "1", Array.Empty<string>(), null, "fabric");

    [Fact]
    public void Optimize_skips_installed_and_second_render_engine()
    {
        var inst = new Instance { GameVersion = "1.21.1", Loader = LoaderType.Fabric };
        var skipped = new List<string>();
        var plan = OptimizeService.Plan(inst, new[] { Mod("lithium-fabric-0.15.jar", "lithium"), Mod("optifabric-1.14.jar", "optifabric") }, skipped);
        Assert.DoesNotContain(plan, m => m.Slug is "sodium" or "lithium" or "embeddium");
        Assert.Contains(plan, m => m.Slug == "ferrite-core");
        Assert.Contains("Lithium", skipped);

        var forge = OptimizeService.Plan(new Instance { GameVersion = "1.20.1", Loader = LoaderType.Forge }, Array.Empty<ModInfo>(), new List<string>());
        Assert.Contains(forge, m => m.Slug == "embeddium");
        Assert.DoesNotContain(forge, m => m.Slug is "sodium" or "lithium");
    }

    [Fact]
    public void Daily_quests_are_stable_and_distinct()
    {
        var day = new DateOnly(2026, 10, 29);
        var a = QuestService.ForDay(day);
        Assert.Equal(3, a.Count);
        Assert.Equal(a, QuestService.ForDay(day));
        Assert.Equal(3, a.Select(q => q.Kind).Distinct().Count());
    }

    [Fact]
    public void Clip_args_take_last_30s_vertical_with_logo()
    {
        var args = ClipService.BuildArgs(@"C:\in game.mp4", @"C:\out.mp4", @"C:\logo.png");
        Assert.Equal("-30", args[args.IndexOf("-sseof") + 1]);
        Assert.True(args.IndexOf("-sseof") < args.IndexOf("-i"), "-sseof должен идти перед входом");
        Assert.Equal(@"C:\in game.mp4", args[args.IndexOf("-i") + 1]);
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("crop=1080:1920", filter);
        Assert.Contains("[1:v]", filter);
        Assert.EndsWith("[v]", filter);
        Assert.Equal(@"C:\out.mp4", args[^1]);

        var plain = ClipService.BuildArgs("a.mp4", "b.mp4", null);
        Assert.DoesNotContain("[1:v]", plain[plain.IndexOf("-filter_complex") + 1]);
        Assert.Single(plain, a => a == "-i");
    }

    [Fact]
    public void Shared_pack_links()
    {
        Assert.True(SharedPackService.IsGistId("aa5a315d61ae9438b18d"));
        Assert.True(SharedPackService.IsGistId("0123456789abcdef0123456789abcdef"));
        Assert.False(SharedPackService.IsGistId("../../etc"));
        Assert.False(SharedPackService.IsGistId(null));
        Assert.Equal("ribbix://pack?gist=abc", SharedPackService.ShareLink("abc"));
    }
}
