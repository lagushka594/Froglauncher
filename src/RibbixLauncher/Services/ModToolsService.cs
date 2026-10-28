using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Что мод сам о себе пишет: fabric.mod.json, quilt.mod.json или META-INF/(neoforge.)mods.toml.</summary>
public sealed record ModInfo(string FileName, bool Enabled, IReadOnlyList<string> Ids, string Name, string Version, IReadOnlyList<string> Depends)
{
    public string Jar => Enabled ? FileName : FileName[..^ModToolsService.DisabledSuffix.Length];
}

public sealed record ModUpdate(string FileName, string Title, string OldVersion, string NewVersion, string Url, string NewFileName, string? Sha1, long Size);

/// <summary>Снимок папки mods перед обновлением; Verified — после него игра хоть раз нормально запустилась.</summary>
public sealed class ModSnapshot
{
    public DateTimeOffset CreatedAt { get; set; }
    public string Reason { get; set; } = "";
    public bool Verified { get; set; }
    public List<InstalledMod> InstalledMods { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public string Directory { get; set; } = "";
}

/// <summary>
/// Общие операции с модами для «Доктора крашей», поиска виновника и «Обновить всё»:
/// метаданные jar, включение/выключение (name.jar ↔ name.jar.disabled), обновления с Modrinth по SHA1,
/// снимки папки mods с откатом.
/// </summary>
public sealed partial class ModToolsService(IHttpClientFactory httpFactory, DownloadService downloads, InstanceService instances,
    ILogger<ModToolsService> log)
{
    public const string DisabledSuffix = ".disabled";
    public const int KeepSnapshots = 3;
    private const string Api = "https://api.modrinth.com/v2/";

    /// <summary>Библиотеки, без которых не работает почти ничего: в поиске виновника они всегда включены.</summary>
    public static readonly HashSet<string> CoreLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric-api", "fabric", "quilted_fabric_api", "qsl", "fabric-language-kotlin", "kotlinforforge", "architectury",
        "cloth-config", "cloth_config", "forge", "neoforge", "minecraft", "java", "fabricloader", "quilt_loader",
    };

    public static string ModsDir(Instance inst) => Path.Combine(inst.GameDirectory, "mods");

    public IReadOnlyList<ModInfo> List(Instance inst)
    {
        var dir = ModsDir(inst);
        if (!Directory.Exists(dir)) return Array.Empty<ModInfo>();
        return Directory.GetFiles(dir, "*.jar").Concat(Directory.GetFiles(dir, "*.jar" + DisabledSuffix))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => Read(f, inst)).ToList();
    }

    private static ModInfo Read(string path, Instance inst)
    {
        var file = Path.GetFileName(path);
        var enabled = !file.EndsWith(DisabledSuffix, StringComparison.Ordinal);
        var jar = enabled ? file : file[..^DisabledSuffix.Length];
        var title = inst.InstalledMods.FirstOrDefault(m => m.FileName == jar)?.Title;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var (ids, name, version, deps) = ReadMetadata(zip);
            return new ModInfo(file, enabled, ids, title ?? name ?? jar, version ?? "", deps);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new ModInfo(file, enabled, Array.Empty<string>(), title ?? jar, "", Array.Empty<string>());
        }
    }

    internal static (IReadOnlyList<string> Ids, string? Name, string? Version, IReadOnlyList<string> Depends) ReadMetadata(ZipArchive zip)
    {
        if (zip.GetEntry("fabric.mod.json") is { } fe && JsonNode.Parse(ReadText(fe), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is JsonObject f)
        {
            var ids = new List<string>();
            if (f["id"]?.GetValue<string>() is { } id) ids.Add(id);
            foreach (var p in f["provides"]?.AsArray() ?? new JsonArray()) if (p?.GetValue<string>() is { } pid) ids.Add(pid);
            var deps = (f["depends"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
            return (ids, f["name"]?.GetValue<string>(), f["version"]?.GetValue<string>(), deps);
        }
        if (zip.GetEntry("quilt.mod.json") is { } qe && JsonNode.Parse(ReadText(qe))?["quilt_loader"] is JsonObject q)
        {
            var ids = q["id"]?.GetValue<string>() is { } id ? new List<string> { id } : new List<string>();
            var deps = (q["depends"]?.AsArray() ?? new JsonArray())
                .Select(d => d is JsonValue v ? v.GetValue<string>() : d?["id"]?.GetValue<string>()).OfType<string>().ToList();
            return (ids, q["metadata"]?["name"]?.GetValue<string>(), q["version"]?.GetValue<string>(), deps);
        }
        var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
        if (toml is not null) return ParseModsToml(ReadText(toml));
        return (Array.Empty<string>(), null, null, Array.Empty<string>());
    }

    /// <summary>Нам нужны только modId, displayName, version и зависимости — полноценный TOML-парсер не нужен.</summary>
    internal static (IReadOnlyList<string>, string?, string?, IReadOnlyList<string>) ParseModsToml(string text)
    {
        var ids = new List<string>();
        var deps = new List<string>();
        string? name = null, version = null, section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[[")) { section = line.Trim('[', ']', ' '); continue; }
            var m = TomlKeyRegex().Match(line);
            if (!m.Success) continue;
            var (key, value) = (m.Groups[1].Value, m.Groups[2].Value);
            if (section == "mods" && key == "modId") ids.Add(value);
            else if (section == "mods" && key == "displayName") name ??= value;
            else if (section == "mods" && key == "version") version ??= value;
            else if (section?.StartsWith("dependencies") == true && key == "modId") deps.Add(value);
        }
        return (ids, name, version, deps);
    }

    [GeneratedRegex(@"^(\w+)\s*=\s*""([^""]*)""$")]
    private static partial Regex TomlKeyRegex();

    private static string ReadText(ZipArchiveEntry e)
    {
        using var r = new StreamReader(e.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Включить/выключить мод, не удаляя. Возвращает новое имя файла.</summary>
    public static string SetEnabled(Instance inst, string fileName, bool enabled)
    {
        var dir = ModsDir(inst);
        var isEnabled = !fileName.EndsWith(DisabledSuffix, StringComparison.Ordinal);
        if (isEnabled == enabled) return fileName;
        var target = enabled ? fileName[..^DisabledSuffix.Length] : fileName + DisabledSuffix;
        var src = Path.Combine(dir, fileName);
        var dst = Path.Combine(dir, target);
        if (File.Exists(dst)) File.Delete(src); // такой файл уже есть — дубликат не нужен
        else File.Move(src, dst);
        return target;
    }

    /// <summary>Файл мода по его modId (из метаданных) или по началу имени файла.</summary>
    public ModInfo? Find(Instance inst, string modId) =>
        List(inst).FirstOrDefault(m => m.Ids.Contains(modId, StringComparer.OrdinalIgnoreCase))
        ?? List(inst).FirstOrDefault(m => m.Jar.StartsWith(modId, StringComparison.OrdinalIgnoreCase));

    // ---------- Обновления ----------

    /// <summary>Новые версии модов с Modrinth: POST /version_files/update по SHA1 включённых jar.</summary>
    public async Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(Instance inst, CancellationToken ct, IReadOnlyCollection<string>? only = null)
    {
        var mods = List(inst).Where(m => m.Enabled && (only is null || only.Contains(m.FileName))).ToList();
        if (mods.Count == 0 || inst.Loader == LoaderType.Vanilla) return Array.Empty<ModUpdate>();
        var byHash = mods.ToDictionary(m => Sha1(Path.Combine(ModsDir(inst), m.FileName)), m => m);
        var body = new JsonObject
        {
            ["hashes"] = new JsonArray(byHash.Keys.Select(h => (JsonNode)h).ToArray()),
            ["algorithm"] = "sha1",
            ["loaders"] = new JsonArray(ModrinthService.Loaders(inst.Loader).Select(l => (JsonNode)l).ToArray()),
            ["game_versions"] = new JsonArray(inst.GameVersion),
        };
        var http = httpFactory.CreateClient(DownloadService.HttpClientName);
        using var resp = await http.PostAsync(Api + "version_files/update", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
        return ParseUpdates(json, byHash);
    }

    internal static IReadOnlyList<ModUpdate> ParseUpdates(JsonObject json, IReadOnlyDictionary<string, ModInfo> byHash)
    {
        var list = new List<ModUpdate>();
        foreach (var (hash, version) in json)
        {
            if (!byHash.TryGetValue(hash, out var mod) || version?["files"] is not JsonArray files) continue;
            // Если среди файлов новой версии есть наш хэш — это та же версия.
            if (files.Any(f => f?["hashes"]?["sha1"]?.GetValue<string>() == hash)) continue;
            var file = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
            if (file?["url"]?.GetValue<string>() is not { } url) continue;
            list.Add(new ModUpdate(mod.FileName, mod.Name, mod.Version, version["version_number"]?.GetValue<string>() ?? "?", url,
                Path.GetFileName(file["filename"]!.GetValue<string>()), file["hashes"]?["sha1"]?.GetValue<string>(), file["size"]?.GetValue<long>() ?? 0));
        }
        return list.OrderBy(u => u.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Снимок → скачать новые файлы → удалить старые. Снимок не помечен проверенным, пока игра не запустится.</summary>
    public async Task<ModSnapshot> ApplyUpdatesAsync(Instance inst, IReadOnlyList<ModUpdate> updates, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var snap = CreateSnapshot(inst, L.T($"Перед обновлением {updates.Count} модов"));
        var dir = ModsDir(inst);
        var items = updates.Select(u => new DownloadItem(u.Url, Path.Combine(dir, u.NewFileName + ".part-update"), u.Sha1, u.Size)).ToList();
        try
        {
            await downloads.DownloadAsync(items, L.T("Обновляем моды"), progress, ct);
        }
        catch
        {
            foreach (var i in items) TryDelete(i.Path);
            throw;
        }
        foreach (var u in updates)
        {
            TryDelete(Path.Combine(dir, u.FileName));
            var dst = Path.Combine(dir, u.NewFileName);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(Path.Combine(dir, u.NewFileName + ".part-update"), dst);
            if (inst.InstalledMods.FirstOrDefault(m => m.FileName == u.FileName) is { } meta) meta.FileName = u.NewFileName;
        }
        instances.Save(inst);
        log.LogInformation("Обновлено модов: {Count} в {Instance}", updates.Count, inst.Id);
        return snap;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    internal static string Sha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    // ---------- Снимки ----------

    public static string SnapshotsDir(Instance inst) => Path.Combine(inst.Directory, "mod-snapshots");

    public ModSnapshot CreateSnapshot(Instance inst, string reason)
    {
        var snap = new ModSnapshot { CreatedAt = DateTimeOffset.Now, Reason = reason, InstalledMods = inst.InstalledMods.Select(Clone).ToList() };
        snap.Directory = Path.Combine(SnapshotsDir(inst), snap.CreatedAt.ToString("yyyyMMdd-HHmmss-fff"));
        var mods = Path.Combine(snap.Directory, "mods");
        Directory.CreateDirectory(mods);
        if (Directory.Exists(ModsDir(inst)))
            foreach (var f in Directory.GetFiles(ModsDir(inst)))
                File.Copy(f, Path.Combine(mods, Path.GetFileName(f)));
        WriteMeta(snap);
        foreach (var old in Snapshots(inst).Skip(KeepSnapshots))
            try { Directory.Delete(old.Directory, true); } catch (IOException) { }
        return snap;
    }

    public IReadOnlyList<ModSnapshot> Snapshots(Instance inst)
    {
        var root = SnapshotsDir(inst);
        if (!Directory.Exists(root)) return Array.Empty<ModSnapshot>();
        return Directory.GetDirectories(root).Select(d =>
        {
            try
            {
                var s = JsonSerializer.Deserialize<ModSnapshot>(File.ReadAllText(Path.Combine(d, "snapshot.json")), Json.Options);
                if (s is not null) s.Directory = d;
                return s;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        }).OfType<ModSnapshot>().OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>Последний снимок, после которого игра ещё ни разу нормально не запустилась, — кандидат на «Вернуть как было».</summary>
    public ModSnapshot? PendingSnapshot(Instance inst) => Snapshots(inst).FirstOrDefault() is { Verified: false } s ? s : null;

    /// <summary>Игра запустилась после обновления — снимок больше не «подозрительный».</summary>
    public void MarkVerified(Instance inst)
    {
        if (PendingSnapshot(inst) is not { } s) return;
        s.Verified = true;
        WriteMeta(s);
    }

    /// <summary>Вернуть папку mods к снимку. Текущие файлы сначала сохраняются отдельным снимком — откат тоже можно отменить.</summary>
    public void Restore(Instance inst, ModSnapshot snap)
    {
        var current = CreateSnapshot(inst, L.T("Перед откатом"));
        current.Verified = true;
        WriteMeta(current);
        var dir = ModsDir(inst);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var f in Directory.GetFiles(Path.Combine(snap.Directory, "mods")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        inst.InstalledMods = snap.InstalledMods.Select(Clone).ToList();
        instances.Save(inst);
        try { Directory.Delete(snap.Directory, true); } catch (IOException) { }
        log.LogInformation("Моды {Instance} возвращены к снимку {Snap}", inst.Id, snap.CreatedAt);
    }

    private static void WriteMeta(ModSnapshot s) =>
        File.WriteAllText(Path.Combine(s.Directory, "snapshot.json"), JsonSerializer.Serialize(s, Json.Options));

    private static InstalledMod Clone(InstalledMod m) => new()
    {
        ProjectId = m.ProjectId, VersionId = m.VersionId, FileName = m.FileName, Title = m.Title, IconUrl = m.IconUrl, IsDependency = m.IsDependency,
    };
}
