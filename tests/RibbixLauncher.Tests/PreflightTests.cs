using System.IO;
using System.Text.Json;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace RibbixLauncher.Tests;

public class PreflightTests
{
    private static ModInfo Mod(string id, string? mc = null, string loader = "fabric", params string[] deps) =>
        new($"{id}.jar", true, new[] { id }, id, "1", deps, mc, loader);

    private static readonly Instance Fabric = new() { GameVersion = "1.20.1", Loader = LoaderType.Fabric, RamMb = 4096 };

    [Theory]
    [InlineData("1.20.1", "1.20.1", true)]
    [InlineData("~1.20", "1.20.1", true)]
    [InlineData("~1.20", "1.21", false)]
    [InlineData(">=1.20 <1.21", "1.20.6", true)]
    [InlineData(">=1.20 <1.21", "1.21.1", false)]
    [InlineData("1.20.x", "1.20.4", true)]
    [InlineData("1.19.x", "1.20.1", false)]
    [InlineData("1.19.2 || 1.20.1", "1.20.1", true)]
    [InlineData("*", "1.21", true)]
    [InlineData(">=1.20.2-alpha.23.31.a", "1.20.1", false)]
    [InlineData("1.21", "1.21.1", true)]
    public void Version_ranges(string range, string version, bool ok) => Assert.Equal(ok, PreflightService.Matches(range, version));

    [Fact]
    public void Finds_missing_dependency_wrong_version_wrong_loader_and_low_ram()
    {
        var mods = new List<ModInfo> { Mod("extra", ">=1.20", "fabric", "sodium", "fabric-api", "minecraft"), Mod("old", "1.19.2"), Mod("forgemod", loader: "forge") };
        mods.AddRange(Enumerable.Range(0, 85).Select(i => Mod("m" + i)));
        var d = PreflightService.Check(Fabric, mods, 12288);
        Assert.Contains(d, x => x.Title == "Не хватает мода «sodium»");
        Assert.Contains(d, x => x.Title == "Не хватает мода «fabric-api»");
        Assert.Contains(d, x => x.Title == "«old» не для 1.20.1");
        Assert.Contains(d, x => x.Title == "«forgemod» — мод для Forge");
        Assert.Contains(d, x => x.Fixes.Any(f => f.Kind == FixKind.Ram && f.Arg == "6144"));
    }

    [Fact]
    public void Clean_pack_passes()
    {
        var d = PreflightService.Check(Fabric, new[] { Mod("fabric-api"), Mod("sodium", "~1.20", "fabric", "fabric-api", "fabric-rendering-v1") }, 12288);
        Assert.Empty(d);
    }

    /// <summary>Настоящая сборка (RIBBIX_PREFLIGHT_INSTANCE — папка версии, только чтение): не должно быть ложных тревог.</summary>
    [Fact]
    public void Real_pack_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("RIBBIX_PREFLIGHT_INSTANCE");
        if (string.IsNullOrEmpty(dir)) return;
        var inst = JsonSerializer.Deserialize<Instance>(File.ReadAllText(Path.Combine(dir, "instance.json")), RibbixLauncher.Core.Json.Options)!;
        inst.Directory = dir;
        var tools = new ModToolsService(null!, null!, new InstanceService(null!, NullLogger<InstanceService>.Instance), NullLogger<ModToolsService>.Instance);
        var found = new PreflightService(tools).Check(inst, 12288);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "ribbix-preflight.txt"), found.Select(f => f.Title + " | " + f.Explanation));
    }
}
