using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace RibbixLauncher.Tests;

public class TransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frog-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly Instance _inst;

    public TransferTests()
    {
        _inst = new Instance { Id = "zz", Name = "Тест сборка", GameVersion = "1.20.1", Loader = LoaderType.Forge, LoaderVersion = "1.20.1-47.2.0", Directory = _root };
        Write("mods/sodium.jar", "SODIUM");
        Write("mods/private.jar", "PRIVATE");
        Write("config/sodium.json", "{}");
        Write("options.txt", "fov:0.5");
        Write("logs/latest.log", "log");
        Write("saves/w/level.dat", "lvl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_inst.GameDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static string Sha1(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        private sealed class H(Func<HttpRequestMessage, HttpResponseMessage> r) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => Task.FromResult(r(req));
        }
        public HttpClient CreateClient(string name) => new(new H(respond));
    }

    private PackTransferService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new FakeHttp(respond), null!, null!, NullLogger<PackTransferService>.Instance);

    [Fact]
    public async Task Mrpack_links_modrinth_files_and_embeds_the_rest()
    {
        var sodium = Sha1("SODIUM");
        var svc = Service(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""{"{{{sodium}}}":{"files":[{"hashes":{"sha1":"{{{sodium}}}"},"url":"https://cdn.modrinth.com/data/AANobbMI/versions/x/sodium.jar"}]}}"""),
        });
        var outFile = Path.Combine(_root, "out.mrpack");
        var r = await svc.ExportAsync(_inst, new ExportOptions(PackFormat.Mrpack, IncludeWorlds: false, IncludeConfigs: true), outFile, null, CancellationToken.None);

        Assert.Equal(1, r.Linked);
        Assert.False(r.ModrinthOffline);
        using var zip = ZipFile.OpenRead(outFile);
        var index = JsonNode.Parse(zip.GetEntry("modrinth.index.json")!.Open())!;
        Assert.Equal("minecraft", index["game"]!.GetValue<string>());
        Assert.Equal("47.2.0", index["dependencies"]!["forge"]!.GetValue<string>());
        var file = Assert.Single(index["files"]!.AsArray());
        Assert.Equal("mods/sodium.jar", file!["path"]!.GetValue<string>());
        Assert.Equal(128, file["hashes"]!["sha512"]!.GetValue<string>().Length);

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("overrides/mods/private.jar", names);
        Assert.Contains("overrides/config/sodium.json", names);
        Assert.Contains("overrides/options.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("logs/") || n.Contains("saves/") || n == "overrides/mods/sodium.jar");
        Assert.Equal(ImportKind.Mrpack, PackTransferService.Detect(outFile));
    }

    [Fact]
    public async Task Mrpack_without_internet_embeds_everything()
    {
        var svc = Service(_ => throw new HttpRequestException("offline"));
        var outFile = Path.Combine(_root, "off.mrpack");
        var r = await svc.ExportAsync(_inst, new ExportOptions(PackFormat.Mrpack, false, true), outFile, null, CancellationToken.None);
        Assert.True(r.ModrinthOffline);
        Assert.Equal(0, r.Linked);
        using var zip = ZipFile.OpenRead(outFile);
        Assert.NotNull(zip.GetEntry("overrides/mods/sodium.jar"));
    }

    [Fact]
    public async Task Full_zip_keeps_settings_and_worlds_on_request()
    {
        _inst.JavaPath = @"C:\Java\bin\javaw.exe";
        _inst.RamMb = 6144;
        var outFile = Path.Combine(_root, "full.zip");
        await Service(_ => throw new InvalidOperationException("сеть не нужна"))
            .ExportAsync(_inst, new ExportOptions(PackFormat.FullZip, IncludeWorlds: true, IncludeConfigs: false), outFile, null, CancellationToken.None);

        using var zip = ZipFile.OpenRead(outFile);
        var manifest = JsonSerializer.Deserialize<Instance>(zip.GetEntry(PackTransferService.RibbixManifest)!.Open(), RibbixLauncher.Core.Json.Options)!;
        Assert.Equal(6144, manifest.RamMb);
        Assert.Null(manifest.JavaPath);
        Assert.NotNull(zip.GetEntry(".minecraft/saves/w/level.dat"));
        Assert.Null(zip.GetEntry(".minecraft/config/sodium.json"));
        Assert.Equal(ImportKind.RibbixZip, PackTransferService.Detect(outFile));
    }

    [Fact]
    public void Mrpack_dependencies_round_trip_for_every_loader()
    {
        foreach (var (loader, version) in new[] { (LoaderType.Fabric, "0.16.5"), (LoaderType.Quilt, "0.26.0"), (LoaderType.NeoForge, "21.1.1"),
                     (LoaderType.Forge, "1.20.1-47.2.0"), (LoaderType.Vanilla, (string?)null) })
        {
            var inst = new Instance { GameVersion = "1.20.1", Loader = loader, LoaderVersion = version };
            var back = ModpackService.MapDependencies(PackTransferService.Dependencies(inst));
            Assert.Equal((loader, version, "1.20.1"), back);
        }
    }

    [Fact]
    public void Prism_components_map_to_loader()
    {
        var pack = JsonNode.Parse("""{"components":[{"uid":"net.minecraft","version":"1.20.1"},{"uid":"net.minecraftforge","version":"47.2.0"}]}""")!;
        Assert.Equal((LoaderType.Forge, "1.20.1-47.2.0", "1.20.1"), PackTransferService.MapPrism(pack));
    }

    [Theory]
    [InlineData(new[] { "modrinth.index.json", "overrides/a.txt" }, ImportKind.Mrpack)]
    [InlineData(new[] { "ribbix-instance.json", ".minecraft/mods/a.jar" }, ImportKind.RibbixZip)]
    [InlineData(new[] { "MyPack/mmc-pack.json", "MyPack/.minecraft/options.txt" }, ImportKind.Prism)]
    [InlineData(new[] { "manifest.json", "overrides/config/a.json" }, ImportKind.CurseForge)]
    [InlineData(new[] { "My World/level.dat", "My World/region/r.0.0.mca" }, ImportKind.World)]
    [InlineData(new[] { "readme.txt" }, ImportKind.Unknown)]
    public void Detects_archive_kind(string[] entries, ImportKind expected)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var e in entries) using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write("x");
        ms.Position = 0;
        using var read = new ZipArchive(ms);
        Assert.Equal(expected, PackTransferService.Detect(read));
    }

    [Fact]
    public void Extraction_blocks_path_traversal()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(z.CreateEntry(".minecraft/../../evil.txt").Open())) w.Write("x");
        ms.Position = 0;
        using var read = new ZipArchive(ms);
        Assert.Throws<ModsException>(() => PackTransferService.ExtractPrefix(read, ".minecraft/", Path.Combine(_root, "dst")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void World_import_finds_root_and_never_overwrites()
    {
        var zipPath = Path.Combine(_root, "Лягушачий мир.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("Лягушачий мир/level.dat").Open())) w.Write("lvl");
            using (var w = new StreamWriter(z.CreateEntry("Лягушачий мир/region/r.0.0.mca").Open())) w.Write("r");
        }
        var svc = new WorldService(NullLogger<WorldService>.Instance);
        Assert.Equal("Лягушачий мир", svc.Import(_inst, zipPath));
        Assert.Equal("Лягушачий мир (2)", svc.Import(_inst, zipPath));
        Assert.True(File.Exists(Path.Combine(_inst.GameDirectory, "saves", "Лягушачий мир (2)", "region", "r.0.0.mca")));

        // Архив без папки мира внутри — имя берётся из имени файла.
        var flat = Path.Combine(_root, "flat.zip");
        using (var z = ZipFile.Open(flat, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("level.dat").Open())) w.Write("lvl");
        Assert.Equal("flat", svc.Import(_inst, flat));
    }

    [Fact]
    public void World_import_rejects_archives_without_level_dat()
    {
        var zipPath = Path.Combine(_root, "junk.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("readme.txt").Open())) w.Write("x");
        Assert.Throws<InvalidDataException>(() => new WorldService(NullLogger<WorldService>.Instance).Import(_inst, zipPath));
    }
}
