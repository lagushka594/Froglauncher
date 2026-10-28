using System.IO;
using System.IO.Compression;
using RibbixLauncher.Core;
using RibbixLauncher.Services;

namespace RibbixLauncher.Tests;

/// <summary>Переезд с FrogLauncher (до 2.6): данные, ссылки, экспорт и пресеты продолжают работать.</summary>
public class RenameTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "ribbix-rename-" + Guid.NewGuid().ToString("N"));

    public RenameTests() => Directory.CreateDirectory(_appData);

    public void Dispose()
    {
        try { Directory.Delete(_appData, true); } catch (IOException) { }
    }

    [Fact]
    public void Old_data_folder_moves_and_paths_are_rewritten()
    {
        var legacy = Path.Combine(_appData, LauncherPaths.LegacyFolder);
        var inst = Path.Combine(legacy, "instances", "wiz");
        Directory.CreateDirectory(inst);
        var javaPath = Path.Combine(legacy, "runtime", "java21", "bin", "javaw.exe").Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(inst, "instance.json"), $"{{\"name\":\"Wizards\",\"javaPath\":\"{javaPath}\"}}");
        File.WriteAllText(Path.Combine(legacy, "playtime.json"), "{}");

        var root = LauncherPaths.ResolveRoot(_appData);

        Assert.Equal(Path.Combine(_appData, "RibbixLauncher"), root);
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(root, "playtime.json")));
        var json = File.ReadAllText(Path.Combine(root, "instances", "wiz", "instance.json"));
        Assert.Contains(Path.Combine(root, "runtime").Replace("\\", "\\\\"), json);
        Assert.DoesNotContain(LauncherPaths.LegacyFolder + "\\\\", json);
        // Второй запуск ничего не трогает.
        Assert.Equal(root, LauncherPaths.ResolveRoot(_appData));
    }

    [Fact]
    public void Fresh_install_uses_new_folder() =>
        Assert.Equal(Path.Combine(_appData, "RibbixLauncher"), LauncherPaths.ResolveRoot(_appData));

    [Theory]
    [InlineData("ribbix://billing/success?sub_id=1")]
    [InlineData("froglauncher://play?mc=1.21.1&l=vanilla")]
    public void Both_link_schemes_are_accepted(string url) => Assert.True(UrlScheme.TryParse(url, out _));

    [Fact]
    public void Old_full_copy_is_still_detected()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(z.CreateEntry(PackTransferService.LegacyManifest).Open())) w.Write("{}");
        ms.Position = 0;
        using var read = new ZipArchive(ms);
        Assert.Equal(ImportKind.RibbixZip, PackTransferService.Detect(read));
    }
}
