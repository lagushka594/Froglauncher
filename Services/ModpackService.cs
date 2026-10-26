using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

public enum PackFormat { Mrpack, CurseForge, Native }

/// <summary>Разобранная сборка: что ставить (MC + загрузчик) и какие файлы скачать.</summary>
public sealed class PackInfo
{
    public PackFormat Format { get; set; }
    public string ZipPath { get; set; } = "";
    public string Name { get; set; } = "Сборка";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "Vanilla";
    public string? LoaderRaw { get; set; }
    public List<(string RelPath, string Url, string? Sha1, long Size)> Files { get; } = new();
    public List<InstalledModMeta> Metas { get; } = new();
    public List<(long ProjectId, long FileId)> CfFiles { get; } = new();
    public string[] OverridePrefixes { get; set; } = { "overrides/" };
}

/// <summary>Импорт/экспорт сборок: Modrinth (.mrpack), CurseForge (.zip) и собственный zip инстанса.</summary>
public sealed class ModpackService
{
    // ---------------------------------------------------------------- Импорт: разбор

    public PackInfo Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var pack = new PackInfo { ZipPath = path };

        var mr = zip.GetEntry("modrinth.index.json");
        if (mr != null)
        {
            var json = ReadJson(mr);
            pack.Format = PackFormat.Mrpack;
            pack.Name = (string?)json["name"] ?? "Сборка Modrinth";
            var deps = json["dependencies"]!.AsObject();
            pack.McVersion = (string)deps["minecraft"]!;
            foreach (var (key, loader) in new[] { ("fabric-loader", "Fabric"), ("quilt-loader", "Quilt"), ("neoforge", "NeoForge"), ("forge", "Forge") })
                if (deps[key] != null) { pack.Loader = loader; pack.LoaderRaw = (string)deps[key]!; break; }

            foreach (var f in json["files"]!.AsArray())
            {
                if ((string?)f!["env"]?["client"] == "unsupported") continue;
                var rel = (string)f["path"]!;
                var url = (string?)f["downloads"]?[0] ?? throw new InvalidDataException("Файл без ссылки: " + rel);
                var sha1 = (string?)f["hashes"]?["sha1"];
                var size = (long?)f["fileSize"] ?? 0;
                pack.Files.Add((rel, url, sha1, size));
                if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    pack.Metas.Add(new InstalledModMeta
                    {
                        Source = "modrinth", FileName = Path.GetFileName(rel), Name = Path.GetFileNameWithoutExtension(rel),
                        Url = url, Sha1 = sha1, Sha512 = (string?)f["hashes"]?["sha512"], Size = size
                    });
            }
            pack.OverridePrefixes = new[] { "overrides/", "client-overrides/" };
            return pack;
        }

        var cf = zip.GetEntry("manifest.json");
        if (cf != null)
        {
            var json = ReadJson(cf);
            if (json["minecraft"] == null) throw new InvalidDataException("manifest.json не похож на сборку CurseForge.");
            pack.Format = PackFormat.CurseForge;
            pack.Name = (string?)json["name"] ?? "Сборка CurseForge";
            pack.McVersion = (string)json["minecraft"]!["version"]!;
            var loaders = json["minecraft"]!["modLoaders"]?.AsArray();
            var primary = loaders?.FirstOrDefault(l => (bool?)l!["primary"] == true) ?? loaders?.FirstOrDefault();
            if (primary != null)
            {
                var id = (string)primary["id"]!;                    // например forge-47.2.0
                var dash = id.IndexOf('-');
                var name = dash > 0 ? id[..dash] : id;
                pack.LoaderRaw = dash > 0 ? id[(dash + 1)..] : null;
                pack.Loader = name.ToLowerInvariant() switch
                { "forge" => "Forge", "fabric" => "Fabric", "quilt" => "Quilt", "neoforge" => "NeoForge", _ => "Vanilla" };
            }
            foreach (var f in json["files"]!.AsArray())
                if ((bool?)f!["required"] != false) pack.CfFiles.Add(((long)f["projectID"]!, (long)f["fileID"]!));
            var ov = (string?)json["overrides"] ?? "overrides";
            pack.OverridePrefixes = new[] { ov.TrimEnd('/') + "/" };
            return pack;
        }

        if (zip.GetEntry("instance.json") != null) { pack.Format = PackFormat.Native; return pack; }

        throw new InvalidDataException("Неизвестный формат сборки. Поддерживаются .mrpack (Modrinth), .zip CurseForge и .zip FrogLauncher.");
    }

    // ---------------------------------------------------------------- Импорт: применение

    /// <summary>Скачивает файлы сборки и распаковывает overrides в папку инстанса. Возвращает список файлов, которые не скачались.</summary>
    public async Task<List<string>> ApplyAsync(PackInfo pack, Instance inst, CurseForgeService cf,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var gameDir = inst.GameDir;
        Directory.CreateDirectory(gameDir);
        var items = new List<DownloadItem>();
        var metas = pack.Metas.ToList();

        foreach (var (rel, url, sha1, size) in pack.Files)
            items.Add(new DownloadItem(url, SafePath(gameDir, rel), sha1, size));

        if (pack.CfFiles.Count > 0)
        {
            var files = await cf.GetFilesByIdsAsync(pack.CfFiles.Select(f => f.FileId), ct);
            foreach (var f in files)
            {
                items.Add(new DownloadItem(f.Url, SafePath(gameDir, "mods/" + f.FileName), f.Sha1, f.Size));
                metas.Add(new InstalledModMeta
                {
                    Source = "curseforge", ProjectId = f.ProjectId, FileId = f.FileId, Name = f.FileName,
                    FileName = f.FileName, Url = f.Url, Sha1 = f.Sha1, Size = f.Size
                });
            }
            // файлы, которые API не вернул (удалены/скрыты)
            var got = files.Select(f => f.FileId).ToHashSet();
            foreach (var (_, fileId) in pack.CfFiles)
                if (!got.Contains(fileId.ToString())) Log.Warn("CurseForge: нет файла " + fileId);
        }

        var failed = await DownloadTolerantAsync(items, progress, ct);
        var failedNames = failed.Select(i => Path.GetFileName(i.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        metas.RemoveAll(m => failedNames.Contains(m.FileName));
        if (metas.Count > 0) ModMetaStore.Save(inst, metas);

        await Task.Run(() => ExtractOverrides(pack, gameDir), ct);
        return failed.Select(i => Path.GetFileName(i.Path)).ToList();
    }

    /// <summary>Сначала качаем пачкой; если что-то упало — докачиваем по одному, собирая неудачи (у части модов CurseForge закрыты ссылки).</summary>
    private static async Task<List<DownloadItem>> DownloadTolerantAsync(List<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var failed = new List<DownloadItem>();
        try { await Downloader.DownloadAsync(items, progress, ct, "Сборка"); return failed; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("Пакетная загрузка сборки не удалась, качаю по одному: " + ex.Message); }

        int done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try { await Downloader.DownloadAsync(new[] { item }, null, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"Не скачан {item.Url}: {ex.Message}"); failed.Add(item); }
            progress?.Report(new DownloadProgress { FilesDone = ++done, FilesTotal = items.Count, Stage = "Сборка" });
        }
        return failed;
    }

    private static void ExtractOverrides(PackInfo pack, string gameDir)
    {
        using var zip = ZipFile.OpenRead(pack.ZipPath);
        foreach (var e in zip.Entries)
        {
            var prefix = pack.OverridePrefixes.FirstOrDefault(p => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix == null || string.IsNullOrEmpty(e.Name)) continue;
            var target = SafePath(gameDir, e.FullName[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    /// <summary>Путь внутри папки инстанса; защита от «../» в именах файлов сборки.</summary>
    private static string SafePath(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Недопустимый путь в сборке: " + rel);
        return full;
    }

    // ---------------------------------------------------------------- Экспорт

    private static readonly string[] OverrideDirs = { "config", "mods", "resourcepacks", "shaderpacks", "defaultconfigs", "kubejs", "scripts" };
    private static readonly string[] OverrideFiles = { "options.txt", "optionsof.txt", "servers.dat" };

    /// <summary>Версия загрузчика в «чистом» виде: 1.20.1-47.2.0 → 47.2.0.</summary>
    public static string? CleanLoaderVersion(Instance inst)
    {
        var v = inst.LoaderVersion;
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (v.StartsWith(inst.McVersion + "-")) v = v[(inst.McVersion.Length + 1)..];
        if (v.EndsWith("-" + inst.McVersion)) v = v[..^(inst.McVersion.Length + 1)];
        return v;
    }

    public void Export(Instance inst, string path, PackFormat format)
    {
        if (File.Exists(path)) File.Delete(path);
        var meta = ModMetaStore.Load(inst);
        var gameDir = inst.GameDir;
        Directory.CreateDirectory(gameDir);

        // какие моды уходят «ссылкой», а какие кладём в overrides
        bool IsLinked(InstalledModMeta m) => format == PackFormat.Mrpack
            ? m.Source == "modrinth" && !string.IsNullOrEmpty(m.Sha1) && !string.IsNullOrEmpty(m.Sha512) && !string.IsNullOrEmpty(m.Url)
            : m.Source == "curseforge" && long.TryParse(m.ProjectId, out _) && long.TryParse(m.FileId, out _);

        var linked = meta.Where(m => IsLinked(m) && File.Exists(Path.Combine(gameDir, "mods", m.FileName))).ToList();
        var linkedNames = linked.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        // 1) overrides
        void AddFile(string abs, string entry) => zip.CreateEntryFromFile(abs, entry, CompressionLevel.Optimal);
        foreach (var dir in OverrideDirs)
        {
            var abs = Path.Combine(gameDir, dir);
            if (!Directory.Exists(abs)) continue;
            foreach (var f in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(gameDir, f).Replace('\\', '/');
                if (dir == "mods" && linkedNames.Contains(Path.GetFileName(f))) continue;
                AddFile(f, "overrides/" + rel);
            }
        }
        foreach (var name in OverrideFiles)
        {
            var abs = Path.Combine(gameDir, name);
            if (File.Exists(abs)) AddFile(abs, "overrides/" + name);
        }

        // 2) манифест
        var loaderVer = CleanLoaderVersion(inst);
        if (format == PackFormat.Mrpack)
        {
            var deps = new JsonObject { ["minecraft"] = inst.McVersion };
            if (loaderVer != null)
            {
                var key = inst.Loader switch
                { "Fabric" => "fabric-loader", "Quilt" => "quilt-loader", "Forge" => "forge", "NeoForge" => "neoforge", _ => null };
                if (key != null) deps[key] = loaderVer;
            }
            var files = new JsonArray();
            foreach (var m in linked)
            {
                files.Add(new JsonObject
                {
                    ["path"] = "mods/" + m.FileName,
                    ["hashes"] = new JsonObject { ["sha1"] = m.Sha1, ["sha512"] = m.Sha512 },
                    ["env"] = new JsonObject { ["client"] = "required", ["server"] = "required" },
                    ["downloads"] = new JsonArray(m.Url),
                    ["fileSize"] = m.Size
                });
            }
            WriteJson(zip, "modrinth.index.json", new JsonObject
            {
                ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = "1.0.0", ["name"] = inst.Name,
                ["summary"] = "Экспорт из FrogLauncher", ["files"] = files, ["dependencies"] = deps
            });
        }
        else
        {
            var loaders = new JsonArray();
            if (loaderVer != null && inst.Loader != "Vanilla")
                loaders.Add(new JsonObject { ["id"] = inst.Loader.ToLowerInvariant() + "-" + loaderVer, ["primary"] = true });
            var files = new JsonArray();
            foreach (var m in linked)
                files.Add(new JsonObject { ["projectID"] = long.Parse(m.ProjectId), ["fileID"] = long.Parse(m.FileId), ["required"] = true });
            WriteJson(zip, "manifest.json", new JsonObject
            {
                ["minecraft"] = new JsonObject { ["version"] = inst.McVersion, ["modLoaders"] = loaders },
                ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1,
                ["name"] = inst.Name, ["version"] = "1.0.0", ["author"] = "FrogLauncher",
                ["files"] = files, ["overrides"] = "overrides"
            });
        }
    }

    private static JsonNode ReadJson(ZipArchiveEntry e)
    {
        using var sr = new StreamReader(e.Open(), Encoding.UTF8);
        return JsonNode.Parse(sr.ReadToEnd())!;
    }

    private static void WriteJson(ZipArchive zip, string name, JsonNode json)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var sw = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        sw.Write(json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
