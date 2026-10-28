using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Достижение из client.jar: data/minecraft/advancement(s)/**.json с блоком display.</summary>
public sealed record AdvancementDef(string Id, string Category, string TitleKey, string DescriptionKey, string Frame, bool Hidden,
    IReadOnlyList<string[]> Requirements);

/// <summary>Достижение с прогрессом игрока: Done из Total групп требований.</summary>
public sealed record AdvancementProgress(AdvancementDef Def, string Title, string Description, int Done, int Total)
{
    public bool IsDone => Done >= Total;
}

/// <summary>Мир с файлом достижений игрока.</summary>
public sealed record AdvancementWorld(Instance Instance, string WorldDir, string Name);

/// <summary>
/// Достижения Minecraft по мирам: полный список и названия — из client.jar и языковых файлов самой игры
/// (так список совпадает с игрой: 102 в 1.19, 122 в 1.21), прогресс — из saves/&lt;мир&gt;/advancements/&lt;uuid&gt;.json.
/// </summary>
public sealed class AdvancementService(InstanceService instances, ILogger<AdvancementService> log)
{
    public static readonly string[] Categories = { "story", "nether", "end", "adventure", "husbandry" };

    private readonly Dictionary<string, IReadOnlyList<AdvancementDef>> _catalogs = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _langs = new();

    /// <summary>Миры всех версий, где есть файл достижений, — сначала самые свежие.</summary>
    public IReadOnlyList<AdvancementWorld> Worlds() => instances.List()
        .SelectMany(inst =>
        {
            var saves = Path.Combine(inst.GameDirectory, "saves");
            return Directory.Exists(saves)
                ? Directory.GetDirectories(saves).Where(w => PlayerFile(w) is not null).Select(w => new AdvancementWorld(inst, w, Path.GetFileName(w)))
                : Enumerable.Empty<AdvancementWorld>();
        })
        .OrderByDescending(w => File.GetLastWriteTimeUtc(PlayerFile(w.WorldDir)!))
        .ToList();

    /// <summary>Файл игрока с наибольшим числом записей (в одиночной игре он один).</summary>
    internal static string? PlayerFile(string worldDir)
    {
        var dir = Path.Combine(worldDir, "advancements");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() : null;
    }

    public IReadOnlyList<AdvancementProgress> Read(AdvancementWorld world, string language)
    {
        var catalog = Catalog(world.Instance.GameVersion);
        if (catalog.Count == 0) return Array.Empty<AdvancementProgress>();
        var lang = Lang(world.Instance.GameVersion, language);
        JsonObject? player = null;
        try { player = JsonNode.Parse(File.ReadAllText(PlayerFile(world.WorldDir)!)) as JsonObject; }
        catch (Exception ex) when (ex is JsonException or IOException) { log.LogWarning(ex, "Не прочитать достижения {World}", world.WorldDir); }
        return Progress(catalog, player, lang);
    }

    /// <summary>Группа требований выполнена, если выполнен любой её критерий (как в игре).</summary>
    public static IReadOnlyList<AdvancementProgress> Progress(IReadOnlyList<AdvancementDef> catalog, JsonObject? player,
        IReadOnlyDictionary<string, string> lang)
    {
        return catalog.Select(def =>
        {
            var entry = player?[def.Id];
            var criteria = (entry?["criteria"] as JsonObject)?.Select(kv => kv.Key).ToHashSet() ?? new HashSet<string>();
            var total = Math.Max(1, def.Requirements.Count);
            var done = entry?["done"]?.GetValue<bool>() == true ? total : def.Requirements.Count(g => g.Any(criteria.Contains));
            return new AdvancementProgress(def, Text(lang, def.TitleKey, def.Id), Text(lang, def.DescriptionKey, ""), done, total);
        }).ToList();
    }

    private static string Text(IReadOnlyDictionary<string, string> lang, string key, string fallback) =>
        lang.TryGetValue(key, out var s) ? s : fallback;

    public IReadOnlyList<AdvancementDef> Catalog(string gameVersion)
    {
        lock (_catalogs)
        {
            if (_catalogs.TryGetValue(gameVersion, out var c)) return c;
            var jar = LauncherPaths.VersionJar(gameVersion);
            try { c = File.Exists(jar) ? ParseCatalog(jar) : Array.Empty<AdvancementDef>(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { log.LogWarning(ex, "Не прочитать {Jar}", jar); c = Array.Empty<AdvancementDef>(); }
            return _catalogs[gameVersion] = c;
        }
    }

    internal static IReadOnlyList<AdvancementDef> ParseCatalog(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        var list = new List<AdvancementDef>();
        foreach (var e in zip.Entries)
        {
            // 1.12–1.20: advancements/, 1.21+: advancement/.
            var m = System.Text.RegularExpressions.Regex.Match(e.FullName, @"^data/minecraft/advancements?/(.+)\.json$");
            if (!m.Success || m.Groups[1].Value.StartsWith("recipes/")) continue;
            try
            {
                using var s = e.Open();
                if (JsonNode.Parse(s) is not JsonObject root || root["display"] is not JsonObject display) continue;
                var path = m.Groups[1].Value;
                var criteria = (root["criteria"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
                var req = root["requirements"] is JsonArray ra
                    ? ra.OfType<JsonArray>().Select(g => g.Select(x => x?.GetValue<string>() ?? "").ToArray()).ToList()
                    : criteria.Select(c => new[] { c }).ToList();
                list.Add(new AdvancementDef("minecraft:" + path, path.Split('/')[0],
                    display["title"]?["translate"]?.GetValue<string>() ?? "",
                    display["description"]?["translate"]?.GetValue<string>() ?? "",
                    display["frame"]?.GetValue<string>() ?? "task",
                    display["hidden"]?.GetValue<bool>() == true, req));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        // Порядок вкладок игры, внутри — корень вкладки первым.
        return list.OrderBy(a => Array.IndexOf(Categories, a.Category) is var i && i < 0 ? 99 : i)
            .ThenBy(a => a.Id.EndsWith("/root") ? 0 : 1).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Строки игры: en_us из client.jar, перевод — из ассетов (minecraft/lang/ru_ru.json в индексе версии).</summary>
    public IReadOnlyDictionary<string, string> Lang(string gameVersion, string language)
    {
        var code = McLanguage(language);
        lock (_langs)
        {
            var key = gameVersion + "|" + code;
            if (_langs.TryGetValue(key, out var d)) return d;
            var map = new Dictionary<string, string>();
            try
            {
                var jar = LauncherPaths.VersionJar(gameVersion);
                if (File.Exists(jar))
                {
                    using var zip = ZipFile.OpenRead(jar);
                    if (zip.GetEntry("assets/minecraft/lang/en_us.json") is { } en) Merge(map, en.Open());
                }
                if (code != "en_us" && AssetPath(gameVersion, $"minecraft/lang/{code}.json") is { } tr) Merge(map, File.OpenRead(tr));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { log.LogWarning(ex, "Не прочитать язык игры {Code}", code); }
            return _langs[key] = map;
        }
    }

    internal static string McLanguage(string language) => language switch
    {
        "ru" => "ru_ru", "es" => "es_es", "de" => "de_de", "pt-BR" => "pt_br", _ => "en_us",
    };

    private static void Merge(Dictionary<string, string> map, Stream s)
    {
        using (s)
            if (JsonNode.Parse(s) is JsonObject o)
                foreach (var (k, v) in o)
                    if (k.StartsWith("advancements.") && v is JsonValue jv && jv.TryGetValue<string>(out var str)) map[k] = str;
    }

    private static string? AssetPath(string gameVersion, string name)
    {
        var json = LauncherPaths.VersionJson(gameVersion);
        if (!File.Exists(json)) return null;
        var indexId = JsonNode.Parse(File.ReadAllText(json))?["assetIndex"]?["id"]?.GetValue<string>();
        var indexPath = Path.Combine(LauncherPaths.Assets, "indexes", indexId + ".json");
        if (indexId is null || !File.Exists(indexPath)) return null;
        var hash = JsonNode.Parse(File.ReadAllText(indexPath))?["objects"]?[name]?["hash"]?.GetValue<string>();
        var path = hash is null ? null : Path.Combine(LauncherPaths.Assets, "objects", hash[..2], hash);
        return path is not null && File.Exists(path) ? path : null;
    }
}
