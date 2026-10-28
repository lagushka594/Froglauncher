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
}
