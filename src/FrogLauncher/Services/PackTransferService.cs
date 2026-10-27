using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

public enum PackFormat { Mrpack, FullZip }

public enum ImportKind { Mrpack, FrogZip, Prism, World, CurseForge, Unknown }

public sealed record ExportOptions(PackFormat Format, bool IncludeWorlds, bool IncludeConfigs, string Version = "1.0.0");

public sealed record ExportResult(string Path, int Linked, int Embedded, bool ModrinthOffline);

/// <summary>
/// Экспорт и импорт сборок.
/// Экспорт: .mrpack (формат Modrinth — открывают Modrinth App, Prism, ATLauncher и FrogLauncher): моды, ресурспаки и
/// шейдеры с Modrinth — ссылками (по SHA1 через API), остальное — в overrides/; либо полная копия .zip для переноса
/// между компьютерами (frog-instance.json + .minecraft).
/// Импорт: .mrpack, полная копия FrogLauncher, экспорт Prism/MultiMC (mmc-pack.json); мир Minecraft (level.dat) —
/// в выбранную версию. CurseForge не поддерживается: его файлы скачиваются только через закрытое API.
/// </summary>
public sealed class PackTransferService(
    IHttpClientFactory httpFactory,
    InstanceService instances,
    ModpackService modpacks,
    ILogger<PackTransferService> log)
{
    public const string FrogManifest = "frog-instance.json";
    private const string Api = "https://api.modrinth.com/v2/";

    /// <summary>Папки, которые не переносим: логи, кэш, отчёты, бэкапы лаунчера.</summary>
    private static readonly string[] SkipDirs = { "logs", "crash-reports", "natives", ".cache", "screenshots", ".fabric", ".mixin.out", "debug" };
    /// <summary>Папки, файлы которых ищем на Modrinth (в .mrpack — ссылками).</summary>
    private static readonly string[] LinkDirs = { "mods", "resourcepacks", "shaderpacks" };

    // ---------- Экспорт ----------

    public async Task<ExportResult> ExportAsync(Instance inst, ExportOptions o, string outPath, IProgress<string>? progress, CancellationToken ct)
    {
        var tmp = outPath + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);
        ExportResult result;
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            result = o.Format == PackFormat.Mrpack
                ? await WriteMrpackAsync(zip, inst, o, progress, ct)
                : WriteFullZip(zip, inst, o, progress);
        File.Move(tmp, outPath, overwrite: true);
        log.LogInformation("Экспорт {Id} → {File}: ссылками {Linked}, внутри {Embedded}", inst.Id, outPath, result.Linked, result.Embedded);
        return result with { Path = outPath };
    }

    /// <summary>Файлы версии для переноса (относительно .minecraft, через «/»).</summary>
    internal static IEnumerable<string> CollectFiles(string game, ExportOptions o)
    {
        if (!Directory.Exists(game)) yield break;
        foreach (var file in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(game, file).Replace('\\', '/');
            var top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "";
            if (SkipDirs.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            if (top.Equals("saves", StringComparison.OrdinalIgnoreCase) && !o.IncludeWorlds) continue;
            if (!o.IncludeConfigs && (top is "config" or "defaultconfigs" or "kubejs" || rel == "options.txt")) continue;
            if (Path.GetFileName(rel) is "session.lock" or "usercache.json" or "launcher_log.txt") continue;
            yield return rel;
        }
    }

    private async Task<ExportResult> WriteMrpackAsync(ZipArchive zip, Instance inst, ExportOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        var game = inst.GameDirectory;
        var files = CollectFiles(game, o).ToList();
        var linkable = files.Where(f => LinkDirs.Any(d => f.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))
                                        && (f.EndsWith(".jar") || f.EndsWith(".zip"))).ToList();

        progress?.Report(L.T($"Считаем хэши: {linkable.Count} файлов"));
        var hashes = new Dictionary<string, (string sha1, string sha512, long size)>();
        foreach (var f in linkable)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(game, f), ct);
            hashes[f] = (Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(), bytes.LongLength);
        }

        progress?.Report(L.T("Ищем файлы на Modrinth"));
        Dictionary<string, string> urls;
        var offline = false;
        try { urls = await LookupModrinthAsync(hashes.Values.Select(h => h.sha1).ToList(), ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Modrinth недоступен — все файлы кладём внутрь сборки");
            urls = new();
            offline = true;
        }

        var index = new MrpackIndex
        {
            FormatVersion = 1, Game = "minecraft", VersionId = o.Version, Name = inst.Name,
            Dependencies = Dependencies(inst),
        };
        var embedded = 0;
        foreach (var f in files)
        {
            if (hashes.TryGetValue(f, out var h) && urls.TryGetValue(h.sha1, out var url))
            {
                index.Files.Add(new MrpackFile
                {
                    Path = f, FileSize = h.size, Downloads = { url },
                    Hashes = new() { ["sha1"] = h.sha1, ["sha512"] = h.sha512 },
                });
                continue;
            }
            AddFile(zip, Path.Combine(game, f), "overrides/" + f);
            embedded++;
        }

        var entry = zip.CreateEntry("modrinth.index.json");
        await using (var s = entry.Open())
            await JsonSerializer.SerializeAsync(s, index, MrpackJson, ct);
        return new ExportResult("", index.Files.Count, embedded, offline);
    }

    private static readonly JsonSerializerOptions MrpackJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>dependencies .mrpack по модлоадеру версии (обратное к ModpackService.MapDependencies).</summary>
    internal static Dictionary<string, string> Dependencies(Instance inst)
    {
        var deps = new Dictionary<string, string> { ["minecraft"] = inst.GameVersion };
        if (inst.LoaderVersion is { Length: > 0 } v)
            switch (inst.Loader)
            {
                case LoaderType.Fabric: deps["fabric-loader"] = v; break;
                case LoaderType.Quilt: deps["quilt-loader"] = v; break;
                case LoaderType.NeoForge: deps["neoforge"] = v; break;
                // В лаунчере Forge хранится как «1.20.1-47.2.0», в .mrpack — только «47.2.0».
                case LoaderType.Forge: deps["forge"] = v.StartsWith(inst.GameVersion + "-") ? v[(inst.GameVersion.Length + 1)..] : v; break;
            }
        return deps;
    }

    /// <summary>POST /version_files: SHA1 → прямая ссылка на файл в cdn.modrinth.com.</summary>
    private async Task<Dictionary<string, string>> LookupModrinthAsync(IReadOnlyList<string> sha1, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (sha1.Count == 0) return result;
        var http = httpFactory.CreateClient(DownloadService.HttpClientName);
        foreach (var chunk in sha1.Chunk(500))
        {
            using var resp = await http.PostAsJsonAsync(Api + "version_files", new { hashes = chunk, algorithm = "sha1" }, ct);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
            foreach (var (hash, version) in json)
            {
                var file = version?["files"]?.AsArray().FirstOrDefault(f => f?["hashes"]?["sha1"]?.GetValue<string>() == hash);
                if (file?["url"]?.GetValue<string>() is { } url && ModpackService.IsAllowedUrl(url)) result[hash] = url;
            }
        }
        return result;
    }

    private static ExportResult WriteFullZip(ZipArchive zip, Instance inst, ExportOptions o, IProgress<string>? progress)
    {
        progress?.Report(L.T("Упаковываем версию"));
        var copy = JsonSerializer.Deserialize<Instance>(JsonSerializer.Serialize(inst, Json.Options), Json.Options)!;
        copy.JavaPath = null; // путь к Java на другом компьютере другой
        var manifest = zip.CreateEntry(FrogManifest);
        using (var s = manifest.Open()) JsonSerializer.Serialize(s, copy, Json.Options);
        var n = 0;
        foreach (var f in CollectFiles(inst.GameDirectory, o))
        {
            AddFile(zip, Path.Combine(inst.GameDirectory, f), ".minecraft/" + f);
            n++;
        }
        return new ExportResult("", 0, n, false);
    }

    private static void AddFile(ZipArchive zip, string src, string name)
    {
        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var es = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        fs.CopyTo(es);
    }

    // ---------- Импорт ----------

    public static ImportKind Detect(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Detect(zip);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ImportKind.Unknown;
        }
    }

    internal static ImportKind Detect(ZipArchive zip)
    {
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        if (names.Contains("modrinth.index.json")) return ImportKind.Mrpack;
        if (names.Contains(FrogManifest)) return ImportKind.FrogZip;
        if (names.Any(n => n == "mmc-pack.json" || n.EndsWith("/mmc-pack.json"))) return ImportKind.Prism;
        if (names.Contains("manifest.json") && names.Any(n => n.StartsWith("overrides/"))) return ImportKind.CurseForge;
        if (names.Any(n => n == "level.dat" || n.EndsWith("/level.dat"))) return ImportKind.World;
        return ImportKind.Unknown;
    }

    /// <summary>Импорт сборки в новую версию (лимит версий тарифа проверяется при создании).</summary>
    public async Task<Instance> ImportAsync(string path, int defaultRamMb, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        switch (Detect(path))
        {
            case ImportKind.Mrpack:
                return await modpacks.InstallFromFileAsync(path, null, null, defaultRamMb, progress, ct);
            case ImportKind.FrogZip:
                return await Task.Run(() => ImportFrogZip(path), ct);
            case ImportKind.Prism:
                return await Task.Run(() => ImportPrism(path, defaultRamMb), ct);
            case ImportKind.CurseForge:
                throw new ModsException(L.T("Сборки CurseForge пока не поддерживаются: их моды скачиваются только через закрытое API CurseForge. Найдите эту сборку в «Сборках» (Modrinth) или экспортируйте её в .mrpack из другого лаунчера."));
            case ImportKind.World:
                throw new ModsException(L.T("Это мир, а не сборка — импортируйте его в нужной версии: «Каталог» → версия → «🌍 Миры» → «Импорт мира»."));
            default:
                throw new ModsException(L.T("Не похоже на сборку: поддерживаются .mrpack, полная копия FrogLauncher (.zip) и экспорт Prism/MultiMC."));
        }
    }

    private Instance ImportFrogZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Instance src;
        using (var s = zip.GetEntry(FrogManifest)!.Open())
            src = JsonSerializer.Deserialize<Instance>(s, Json.Options) ?? throw new ModsException(L.T("Повреждён frog-instance.json"));
        var inst = instances.Create(src.Name, src.GameVersion, src.Loader, src.LoaderVersion, src.RamMb);
        try
        {
            inst.LaunchVersionId = src.LaunchVersionId;
            inst.RamMinMb = src.RamMinMb; inst.Gc = src.Gc; inst.Priority = src.Priority; inst.PreferDiscreteGpu = src.PreferDiscreteGpu;
            inst.JvmArgs = src.JvmArgs; inst.Width = src.Width; inst.Height = src.Height; inst.Fullscreen = src.Fullscreen;
            inst.IconUrl = src.IconUrl; inst.InstalledMods = src.InstalledMods;
            ExtractPrefix(zip, ".minecraft/", inst.GameDirectory);
            instances.Save(inst);
            return inst;
        }
        catch
        {
            instances.Delete(inst);
            throw;
        }
    }

    /// <summary>Prism Launcher / MultiMC: mmc-pack.json (компоненты) + папка .minecraft или minecraft.</summary>
    private Instance ImportPrism(string path, int ramMb)
    {
        using var zip = ZipFile.OpenRead(path);
        var packEntry = zip.Entries.First(e => e.FullName.Replace('\\', '/').EndsWith("mmc-pack.json"));
        var root = packEntry.FullName.Replace('\\', '/')[..^"mmc-pack.json".Length];
        JsonNode pack;
        using (var s = packEntry.Open()) pack = JsonNode.Parse(s) ?? throw new ModsException(L.T("Повреждён mmc-pack.json"));
        var (loader, loaderVersion, mc) = MapPrism(pack);

        var name = Path.GetFileNameWithoutExtension(path);
        var cfg = zip.GetEntry(root + "instance.cfg");
        if (cfg is not null)
        {
            using var r = new StreamReader(cfg.Open());
            while (r.ReadLine() is { } line)
                if (line.StartsWith("name=", StringComparison.Ordinal) && line.Length > 5) name = line[5..].Trim();
        }

        var inst = instances.Create(name, mc, loader, loaderVersion, ramMb);
        try
        {
            var game = zip.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith(root + ".minecraft/")) ? root + ".minecraft/" : root + "minecraft/";
            ExtractPrefix(zip, game, inst.GameDirectory);
            instances.Save(inst);
            return inst;
        }
        catch
        {
            instances.Delete(inst);
            throw;
        }
    }

    internal static (LoaderType loader, string? version, string mc) MapPrism(JsonNode pack)
    {
        string? Uid(string uid) => pack["components"]?.AsArray()
            .FirstOrDefault(c => c?["uid"]?.GetValue<string>() == uid)?["version"]?.GetValue<string>();
        var mc = Uid("net.minecraft") ?? throw new ModsException(L.T("В сборке не указана версия Minecraft"));
        if (Uid("net.fabricmc.fabric-loader") is { } fabric) return (LoaderType.Fabric, fabric, mc);
        if (Uid("org.quiltmc.quilt-loader") is { } quilt) return (LoaderType.Quilt, quilt, mc);
        if (Uid("net.neoforged") is { } neo) return (LoaderType.NeoForge, neo, mc);
        if (Uid("net.minecraftforge") is { } forge) return (LoaderType.Forge, $"{mc}-{forge}", mc);
        return (LoaderType.Vanilla, null, mc);
    }

    /// <summary>Распаковать всё под prefix в папку; ../ запрещены; общий объём ограничен (защита от zip-бомб).</summary>
    internal static void ExtractPrefix(ZipArchive zip, string prefix, string dst, long maxBytes = 32L << 30)
    {
        var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)
                                             && !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        if (entries.Sum(e => e.Length) > maxBytes) throw new ModsException(L.T("Архив слишком большой для распаковки"));
        foreach (var e in entries)
        {
            var target = ModpackService.SafePath(dst, e.FullName.Replace('\\', '/')[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }
}
