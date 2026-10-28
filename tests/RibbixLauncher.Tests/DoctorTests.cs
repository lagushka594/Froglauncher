using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace RibbixLauncher.Tests;

public class DoctorTests
{
    private static ModInfo Mod(string jar, string id, params string[] deps) => new(jar, true, new[] { id }, id, "1.0", deps);

    private static CrashDoctor.Context Ctx(bool snapshot = false, int ram = 4096, params ModInfo[] mods) =>
        new(new Instance { GameVersion = "1.21.1", Loader = LoaderType.Fabric, RamMb = ram }, mods, 12288, snapshot);

    private static readonly ModInfo[] TwoMods = { Mod("sodium.jar", "sodium"), Mod("extra.jar", "sodium-extra", "sodium") };

    [Fact]
    public void Fabric_missing_dependency_offers_download()
    {
        const string log = "net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible with the game or each other!\n" +
                           "\t - Mod 'Sodium Extra' (sodium-extra) 0.5.1+mc1.21.1 requires version 0.5.0 or later of mod 'Sodium' (sodium), which is missing!";
        var d = CrashDoctor.Diagnose(log, Ctx(mods: TwoMods))[0];
        Assert.Equal("Не хватает мода «Sodium»", d.Title);
        Assert.Equal((FixKind.InstallMod, "sodium"), (d.Fixes[0].Kind, d.Fixes[0].Arg));
        Assert.Equal((FixKind.DisableMod, "sodium-extra"), (d.Fixes[1].Kind, d.Fixes[1].Arg));
    }

    [Fact]
    public void Fabric_old_style_missing_fabric_api()
    {
        const string log = "- Mod 'Iris' (iris) 1.7.0 requires any version of fabric-api, which is missing!";
        var d = CrashDoctor.Diagnose(log, Ctx(mods: TwoMods))[0];
        Assert.Equal(FixKind.InstallMod, d.Fixes[0].Kind);
        Assert.Equal("fabric-api", d.Fixes[0].Arg);
    }

    [Fact]
    public void Mod_for_other_minecraft_offers_update()
    {
        const string log = "- Mod 'Sodium' (sodium) 0.5.3 requires version 1.20.1 of 'Minecraft' (minecraft), but only the wrong version is present: 1.21.1!";
        var d = CrashDoctor.Diagnose(log, Ctx(mods: TwoMods))[0];
        Assert.Equal("«Sodium» не работает на 1.21.1", d.Title);
        Assert.Equal(FixKind.UpdateMod, d.Fixes[0].Kind);
        Assert.Equal("sodium", d.Fixes[0].Arg);
    }

    [Fact]
    public void Forge_missing_dependency()
    {
        const string log = "Missing or unsupported mandatory dependencies:\n\tMod ID: 'geckolib', Requested by: 'mowziesmobs', Expected range: '[4.4.4,)', Actual version: '[MISSING]'";
        var d = CrashDoctor.Diagnose(log, Ctx())[0];
        Assert.Equal("Не хватает мода «geckolib»", d.Title);
        Assert.Equal("geckolib", d.Fixes[0].Arg);
    }

    [Theory]
    [InlineData("java.lang.UnsupportedClassVersionError: net/x/Mod has been compiled by a more recent version of the Java Runtime (class file version 65.0)", 21)]
    [InlineData("Unsupported class file major version 61", 17)]
    [InlineData("class jdk.internal.loader.ClassLoaders$AppClassLoader cannot be cast to class java.net.URLClassLoader", 8)]
    public void Java_version_is_detected(string log, int major)
    {
        var d = CrashDoctor.Diagnose(log, Ctx()).First(x => x.Fixes.Any(f => f.Kind == FixKind.Java));
        Assert.Equal(major.ToString(), d.Fixes[0].Arg);
    }

    [Fact]
    public void Out_of_memory_gives_more_ram_but_not_above_safe()
    {
        var d = CrashDoctor.Diagnose("java.lang.OutOfMemoryError: Java heap space", Ctx(ram: 4096))[0];
        Assert.Equal((FixKind.Ram, "6144"), (d.Fixes[0].Kind, d.Fixes[0].Arg));
        var capped = CrashDoctor.Diagnose("java.lang.OutOfMemoryError: Java heap space", Ctx(ram: 12288))[0];
        Assert.Empty(capped.Fixes);
    }

    [Fact]
    public void Driver_problem_opens_driver_page()
    {
        var d = CrashDoctor.Diagnose("GLFW error 65542: WGL: The driver does not appear to support OpenGL", Ctx())[0];
        Assert.Equal(FixKind.Drivers, d.Fixes[0].Kind);
    }

    [Fact]
    public void Unknown_crash_offers_bisect_and_snapshot_rollback()
    {
        var list = CrashDoctor.Diagnose("Exception in thread main: something odd", Ctx(snapshot: true, mods: TwoMods));
        Assert.Equal(FixKind.Rollback, list[0].Fixes[0].Kind);
        Assert.Contains(list, d => d.Fixes.Any(f => f.Kind == FixKind.Bisect));
    }

    [Fact]
    public void Suspected_mod_from_crash_report()
    {
        const string report = "---- Minecraft Crash Report ----\nSuspected Mods: Sodium Extra (sodium-extra)\n";
        var d = CrashDoctor.Diagnose(report, Ctx(mods: TwoMods))[0];
        Assert.Equal("Похоже, виноват «sodium-extra»", d.Title);
        Assert.Equal(FixKind.UpdateMod, d.Fixes[0].Kind);
    }

    [Fact]
    public void Duplicate_mod_files()
    {
        var mods = new[] { Mod("sodium-0.5.jar", "sodium"), Mod("sodium-0.6.jar", "sodium") };
        var d = CrashDoctor.Diagnose("Found 2 mods with the id sodium", Ctx(mods: mods))[0];
        Assert.Equal("file:sodium-0.6.jar", d.Fixes[0].Arg);
    }
}

public class ModToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frog-mods-" + Guid.NewGuid().ToString("N"));
    private readonly Instance _inst;
    private readonly ModToolsService _tools;

    public ModToolsTests()
    {
        _inst = new Instance { Id = "m", Name = "M", GameVersion = "1.21.1", Loader = LoaderType.Fabric, Directory = _root };
        Directory.CreateDirectory(ModToolsService.ModsDir(_inst));
        var instances = new InstanceService(null!, NullLogger<InstanceService>.Instance);
        _tools = new ModToolsService(null!, null!, instances, NullLogger<ModToolsService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Jar(string name, string entry, string text)
    {
        using var z = ZipFile.Open(Path.Combine(ModToolsService.ModsDir(_inst), name), ZipArchiveMode.Create);
        using var w = new StreamWriter(z.CreateEntry(entry).Open());
        w.Write(text);
    }

    [Fact]
    public void Reads_fabric_and_forge_metadata()
    {
        Jar("extra.jar", "fabric.mod.json", """{"id":"sodium-extra","name":"Sodium Extra","version":"0.5","depends":{"sodium":">=0.5","minecraft":"1.21.1"},"provides":["extra"]}""");
        Jar("gecko.jar.disabled", "META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"geckolib\"\ndisplayName=\"GeckoLib\"\nversion=\"4.4\"\n[[dependencies.geckolib]]\nmodId=\"forge\"\n");
        var list = _tools.List(_inst);
        var extra = list.Single(m => m.Jar == "extra.jar");
        Assert.Equal(new[] { "sodium-extra", "extra" }, extra.Ids);
        Assert.Contains("sodium", extra.Depends);
        var gecko = list.Single(m => m.Jar == "gecko.jar");
        Assert.False(gecko.Enabled);
        Assert.Equal("GeckoLib", gecko.Name);
        Assert.Equal(new[] { "forge" }, gecko.Depends);
        Assert.Equal("gecko.jar", ModToolsService.SetEnabled(_inst, gecko.FileName, true));
        Assert.True(File.Exists(Path.Combine(ModToolsService.ModsDir(_inst), "gecko.jar")));
    }

    [Fact]
    public void Updates_skip_same_version()
    {
        var mod = new ModInfo("a.jar", true, new[] { "a" }, "A", "1.0", Array.Empty<string>());
        var json = (JsonObject)JsonNode.Parse("""
            {"h1":{"version_number":"2.0","files":[{"primary":true,"url":"https://cdn.modrinth.com/a2.jar","filename":"a-2.0.jar","hashes":{"sha1":"n1"},"size":5}]},
             "h2":{"version_number":"1.0","files":[{"url":"https://cdn.modrinth.com/b.jar","filename":"b.jar","hashes":{"sha1":"h2"}}]}}
            """)!;
        var updates = ModToolsService.ParseUpdates(json, new Dictionary<string, ModInfo> { ["h1"] = mod, ["h2"] = mod with { FileName = "b.jar" } });
        var u = Assert.Single(updates);
        Assert.Equal(("1.0", "2.0", "a-2.0.jar"), (u.OldVersion, u.NewVersion, u.NewFileName));
    }

    [Fact]
    public void Snapshot_restore_round_trip_and_pending_flag()
    {
        Jar("old.jar", "fabric.mod.json", """{"id":"old"}""");
        var snap = _tools.CreateSnapshot(_inst, "test");
        Assert.NotNull(_tools.PendingSnapshot(_inst));
        File.Delete(Path.Combine(ModToolsService.ModsDir(_inst), "old.jar"));
        Jar("new.jar", "fabric.mod.json", """{"id":"new"}""");
        _tools.Restore(_inst, snap);
        Assert.Equal(new[] { "old.jar" }, _tools.List(_inst).Select(m => m.FileName));
        // Откат тоже сохранил снимок «как было до отката» — и он уже проверенный.
        var back = Assert.Single(_tools.Snapshots(_inst));
        Assert.True(back.Verified);
        Assert.Null(_tools.PendingSnapshot(_inst));
    }
}

public class BisectTests
{
    private static ModInfo Mod(string jar, string id, params string[] deps) => new(jar, true, new[] { id }, id, "1", deps);

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(64)]
    [InlineData(99)]
    public async Task Finds_culprit_in_about_log2_launches(int culpritIndex)
    {
        var all = Enumerable.Range(0, 100).Select(i => Mod($"m{i}.jar", $"m{i}")).Append(Mod("fabric-api.jar", "fabric-api")).ToList();
        var culprit = $"m{culpritIndex}.jar";
        var launched = new List<List<string>>();
        var (found, launches, reproduced) = await ModBisectService.SearchAsync(all, set =>
        {
            launched.Add(set);
            return Task.FromResult(set.Contains(culprit));
        }, null, CancellationToken.None);
        Assert.True(reproduced);
        Assert.Equal(culprit, found);
        Assert.InRange(launches, 7, 8);
        // Библиотеки включены всегда.
        Assert.All(launched, s => Assert.Contains("fabric-api.jar", s));
    }

    [Fact]
    public async Task No_crash_with_everything_means_not_reproduced()
    {
        var (found, launches, reproduced) = await ModBisectService.SearchAsync(new[] { Mod("a.jar", "a"), Mod("b.jar", "b") },
            _ => Task.FromResult(false), null, CancellationToken.None);
        Assert.Equal((null, 1, false), (found, launches, reproduced));
    }

    [Fact]
    public void Closure_keeps_dependencies_and_libraries()
    {
        var all = new[] { Mod("api.jar", "fabric-api"), Mod("sodium.jar", "sodium"), Mod("extra.jar", "sodium-extra", "sodium"), Mod("x.jar", "x") };
        Assert.Equal(new[] { "api.jar", "sodium.jar", "extra.jar" }, ModBisectService.Closure(all, new[] { "extra.jar" }));
    }
}

public class FriendLinkTests
{
    private static readonly FriendInvite Invite = new("Wizards RPG", "1.21.1", LoaderType.Fabric, "0.16.5", "play.example.net:25565",
        new[] { ("mods", "AANobbMI"), ("resourcepacks", "Xy12AbCd"), ("shaderpacks", "QwErTy99") });

    [Fact]
    public void Link_round_trip()
    {
        var back = FriendLinkService.Parse(new Uri("ribbix://play?" + FriendLinkService.Query(Invite)))!;
        Assert.Equal(Invite.Name, back.Name);
        Assert.Equal((LoaderType.Fabric, "0.16.5", "play.example.net:25565"), (back.Loader, back.LoaderVersion, back.Server));
        Assert.Equal(Invite.Files, back.Files);
        Assert.Equal(Invite.Key, back.Key);
        // Та же ссылка через https-страницу API.
        Assert.NotNull(FriendLinkService.Parse(new Uri("https://api.frog.example/play?" + FriendLinkService.Query(Invite))));
    }

    [Theory]
    [InlineData("ribbix://play?mc=1.21.1&l=fabric&f=../../evil")]
    [InlineData("ribbix://play?mc=1.21;rm&l=fabric")]
    [InlineData("ribbix://play?mc=1.21.1&l=bukkit")]
    [InlineData("ribbix://billing/success")]
    public void Rejects_bad_links(string url) => Assert.Null(FriendLinkService.Parse(new Uri(url)));

    [Fact]
    public void Bad_server_is_dropped()
    {
        var i = FriendLinkService.Parse(new Uri("ribbix://play?mc=1.21.1&l=vanilla&s=" + Uri.EscapeDataString("evil host/../x")))!;
        Assert.Null(i.Server);
    }
}

public class FpsTests
{
    [Fact]
    public void Mojang_and_tiny_mappings()
    {
        const string proguard = "net.minecraft.client.Main -> abc:\n    int x -> a\nnet.minecraft.client.Minecraft -> fmf:\n# {\"fileName\":\"Minecraft.java\"}\n" +
                                "    java.lang.String fpsString -> A\n    int fps -> bh\n    1486:1486:int getFps() -> o\nnet.minecraft.Other -> b:\n";
        Assert.Equal(("fmf", "bh"), FpsBenchmarkService.ParseMojangMappings(proguard, "net.minecraft.client.Minecraft", "fps"));
        const string tiny = "tiny\t2\t0\tofficial\tintermediary\nc\tabc\tnet/minecraft/class_1\n\tf\tI\tbh\tfield_9\nc\tfmf\tnet/minecraft/class_310\n\tm\t()I\to\tmethod_1\n\tf\tI\tbh\tfield_1738\n";
        Assert.Equal(("net.minecraft.class_310", "field_1738"), FpsBenchmarkService.ParseTiny(tiny, "fmf", "bh"));
    }

    /// <summary>Настоящие маппинги 1.21.2, если скачаны (RIBBIX_MAPPINGS_DIR с client.txt и inter.jar).</summary>
    [Fact]
    public void Real_mappings_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("RIBBIX_MAPPINGS_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var obf = FpsBenchmarkService.ParseMojangMappings(File.ReadAllText(Path.Combine(dir, "client.txt")), "net.minecraft.client.Minecraft", "fps");
        Assert.Equal(("fmf", "bh"), obf);
        using var z = ZipFile.OpenRead(Path.Combine(dir, "inter.jar"));
        using var r = new StreamReader(z.GetEntry("mappings/mappings.tiny")!.Open());
        Assert.Equal(("net.minecraft.class_310", "field_1738"), FpsBenchmarkService.ParseTiny(r.ReadToEnd(), "fmf", "bh"));
    }

    [Fact]
    public void Helpers()
    {
        Assert.Equal(55, FpsBenchmarkService.Percentile(new double[] { 90, 40, 60, 100, 80, 70, 65, 75, 85, 95, 55 }, 0.1));
        Assert.True(FpsBenchmarkService.IsBefore("1.20.4", 1, 20, 5));
        Assert.False(FpsBenchmarkService.IsBefore("1.21", 1, 20, 5));
        Assert.True(FpsBenchmarkService.KeepFile("level.dat", 0, 0));
        Assert.True(FpsBenchmarkService.KeepFile("region/r.1.-1.mca", 0, 0));
        Assert.False(FpsBenchmarkService.KeepFile("region/r.5.0.mca", 0, 0));
        Assert.False(FpsBenchmarkService.KeepFile("DIM-1/region/r.0.0.mca", 0, 0));
        Assert.False(FpsBenchmarkService.KeepFile("session.lock", 0, 0));
        Assert.True(FpsBenchmarkService.KeepFile("playerdata/x.dat", 0, 0));
    }

    [Fact]
    public void Agent_is_embedded()
    {
        using var s = typeof(FpsBenchmarkService).Assembly.GetManifestResourceStream("RibbixLauncher.ribbix-fps-agent.jar");
        Assert.NotNull(s);
        using var z = new ZipArchive(s!);
        Assert.NotNull(z.GetEntry("ribbix/fps/RibbixFps.class"));
        using var m = new StreamReader(z.GetEntry("META-INF/MANIFEST.MF")!.Open());
        Assert.Contains("Premain-Class: ribbix.fps.RibbixFps", m.ReadToEnd());
    }
}

public class TimelapseTests
{
    [Fact]
    public void Unpacks_both_long_layouts()
    {
        // 5 бит: 12 значений на long без переноса (1.16+).
        long packed = 0;
        for (var i = 0; i < 12; i++) packed |= (long)(i + 1) << (i * 5);
        Assert.Equal(7, MapRenderer.Unpack(new[] { packed }, 6, 5, spanning: false));
        // С переносом (до 1.16): 13-е значение начинается в 60-м бите первого long.
        var data = new long[2];
        data[0] = 0b1011L << 60;
        data[1] = 0b1;
        Assert.Equal(0b11011, MapRenderer.Unpack(data, 12, 5, spanning: true));
    }

    [Fact]
    public void Gif_decodes_with_expected_colors()
    {
        const int w = 40, h = 30;
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { px[i * 4] = 0x3E; px[i * 4 + 1] = 0xA8; px[i * 4 + 2] = 0x6A; px[i * 4 + 3] = 255; } // трава
        px[0] = px[1] = px[2] = 255; // белый пиксель
        var ms = new MemoryStream();
        using (var gif = new GifWriter(ms, w, h))
        {
            gif.AddFrame(px, 50);
            gif.AddFrame(px, 50);
        }
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                ms.Position = 0;
                var dec = new System.Windows.Media.Imaging.GifBitmapDecoder(ms, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Assert.Equal(2, dec.Frames.Count);
                var f = new System.Windows.Media.Imaging.FormatConvertedBitmap(dec.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var outPx = new byte[w * h * 4];
                f.CopyPixels(outPx, w * 4, 0);
                Assert.Equal(new byte[] { 255, 255, 255 }, outPx[..3]);
                Assert.Equal(new byte[] { 51, 153, 102 }, outPx[4..7]); // ближайший цвет куба 6×6×6
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }

    /// <summary>Настоящий мир (RIBBIX_WORLD_DIR, только чтение) → PNG в RIBBIX_MAP_OUT.</summary>
    [Fact]
    public void Real_world_map_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("RIBBIX_WORLD_DIR");
        var outFile = Environment.GetEnvironmentVariable("RIBBIX_MAP_OUT");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(outFile)) return;
        var (cx, cz) = TimelapseService.CenterOf(Path.Combine(dir, "level.dat"));
        var px = MapRenderer.Render(new FolderRegions(dir), cx, cz, 256);
        Assert.Contains(px.Chunk(4), p => p[0] != 0x16);
        var t = new Thread(() =>
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(256, 256, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 256 * 4);
            YearRecapService.Save(bmp, outFile);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }
}

public class TimelapseRealTests
{
    /// <summary>Таймлапс настоящего мира: RIBBIX_TL_INSTANCE (папка версии, только чтение) → RIBBIX_TL_OUT.</summary>
    [Fact]
    public void Real_timelapse_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("RIBBIX_TL_INSTANCE");
        var outDir = Environment.GetEnvironmentVariable("RIBBIX_TL_OUT");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(outDir)) return;
        var inst = new Instance { Id = "tl", Name = "tl", Directory = dir };
        var worlds = new WorldService(NullLogger<WorldService>.Instance);
        var world = worlds.List(inst).First();
        Exception? error = null;
        TimelapseResult? r = null;
        var t = new Thread(() =>
        {
            try { r = TimelapseService.Create(inst, world, world.Backups, 256, outDir, null, CancellationToken.None); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal(world.Backups.Count + 1, r!.Frames);
    }
}
