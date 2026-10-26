using System.Text.Json.Nodes;
using FrogLauncher.Core;

namespace FrogLauncher.Services;

/// <summary>Элемент библиотеки после применения правил.</summary>
public sealed record LibFile(string Name, string AbsPath, string Url, string? Sha1, long Size, bool OnClasspath, bool Extract);

/// <summary>Работа с version.json: загрузка, слияние inheritsFrom, правила, библиотеки.</summary>
public static class VersionJson
{
    public static string Dir(string id) => Path.Combine(AppPaths.Versions, id);
    public static string JsonPath(string id) => Path.Combine(Dir(id), id + ".json");
    public static string JarPath(string id) => Path.Combine(Dir(id), id + ".jar");
    public static bool Exists(string id) => File.Exists(JsonPath(id));

    public static JsonObject Load(string id)
    {
        var p = JsonPath(id);
        if (!File.Exists(p)) throw new FileNotFoundException($"Версия «{id}» не установлена (нет {id}.json).");
        return JsonNode.Parse(File.ReadAllText(p))!.AsObject();
    }

    /// <summary>Загружает версию и сливает её с родителем (Fabric/Forge/Quilt используют inheritsFrom).</summary>
    public static JsonObject LoadMerged(string id)
    {
        var child = Load(id);
        var parentId = (string?)child["inheritsFrom"];
        if (string.IsNullOrEmpty(parentId))
        {
            child["_jarId"] = (string?)child["jar"] ?? id;
            return child;
        }

        var parent = LoadMerged(parentId);
        var m = (JsonObject)parent.DeepClone();
        foreach (var kv in child)
        {
            if (kv.Key is "libraries" or "arguments" or "inheritsFrom") continue;
            m[kv.Key] = kv.Value?.DeepClone();
        }

        // библиотеки: сначала дочерние, затем родительские, без дублей по group:artifact[:classifier]
        var libs = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var src in new[] { child["libraries"], parent["libraries"] })
        {
            if (src is not JsonArray arr) continue;
            foreach (var l in arr)
            {
                var key = Maven.Key((string?)l?["name"] ?? "");
                if (key != "" && !seen.Add(key)) continue;
                libs.Add(l!.DeepClone());
            }
        }
        m["libraries"] = libs;

        // аргументы: родитель + потомок
        if (parent["arguments"] != null || child["arguments"] != null)
        {
            var args = new JsonObject();
            foreach (var k in new[] { "jvm", "game" })
            {
                var a = new JsonArray();
                foreach (var s in new[] { parent["arguments"]?[k], child["arguments"]?[k] })
                    if (s is JsonArray sa) foreach (var e in sa) a.Add(e!.DeepClone());
                args[k] = a;
            }
            m["arguments"] = args;
        }
        if (child["minecraftArguments"] != null) m["minecraftArguments"] = child["minecraftArguments"]!.DeepClone();

        if (child["jar"] != null) m["_jarId"] = (string?)child["jar"];
        else if (child["downloads"]?["client"] != null) m["_jarId"] = id;
        m["id"] = id;
        return m;
    }

    public static int? JavaMajor(JsonObject merged) => (int?)merged["javaVersion"]?["majorVersion"];
}

/// <summary>Вычисление правил (rules) из version.json для Windows x64.</summary>
public static class Rules
{
    public static bool Allowed(JsonNode? rules, ISet<string>? features)
    {
        if (rules is not JsonArray arr || arr.Count == 0) return true;
        bool allowed = false;
        foreach (var r in arr)
        {
            if (r is not JsonObject rule) continue;
            bool match = true;

            if (rule["os"] is JsonObject os)
            {
                var name = (string?)os["name"];
                if (name != null && name != "windows") match = false;
                var arch = (string?)os["arch"];
                if (arch != null && arch is "x86" or "arm64") match = false;
            }
            if (match && rule["features"] is JsonObject f)
            {
                foreach (var kv in f)
                {
                    bool want = (bool?)kv.Value ?? false;
                    bool have = features?.Contains(kv.Key) ?? false;
                    if (want != have) { match = false; break; }
                }
            }
            if (match) allowed = (string?)rule["action"] == "allow";
        }
        return allowed;
    }
}

public static class Libraries
{
    public static List<LibFile> Resolve(JsonObject v)
    {
        var res = new List<LibFile>();
        if (v["libraries"] is not JsonArray arr) return res;

        foreach (var node in arr)
        {
            if (node is not JsonObject lib) continue;
            if (!Rules.Allowed(lib["rules"], null)) continue;

            var name = (string?)lib["name"] ?? "";
            var dl = lib["downloads"];
            string? natives = null;
            if (lib["natives"]?["windows"] is JsonNode nn) natives = ((string)nn!).Replace("${arch}", "64");

            // старый формат нативов: classifiers → распаковка
            if (natives != null)
            {
                var c = dl?["classifiers"]?[natives];
                if (c != null) res.Add(FromDownload(name, c, false, true));
                else if (dl == null)
                {
                    var rel = Maven.ToPath(name + ":" + natives);
                    res.Add(new LibFile(name, Abs(rel), Url(lib, rel), null, 0, false, true));
                }
            }

            var art = dl?["artifact"];
            if (art != null)
                res.Add(FromDownload(name, art, true, name.Contains(":natives-windows")));
            else if (dl == null && natives == null && name != "")
            {
                // Fabric/Quilt/старый Forge: только name (+ url репозитория)
                var rel = Maven.ToPath(name);
                res.Add(new LibFile(name, Abs(rel), Url(lib, rel), (string?)lib["sha1"], 0, true, false));
            }
        }
        return res;
    }

    private static string Abs(string rel) => Path.Combine(AppPaths.Libraries, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Url(JsonObject lib, string rel)
    {
        var baseUrl = (string?)lib["url"] ?? ApiConfig.LibrariesBase;
        if (baseUrl.StartsWith(ApiConfig.ForgeLegacyHost)) baseUrl = ApiConfig.ForgeNewHost + baseUrl[ApiConfig.ForgeLegacyHost.Length..];
        if (baseUrl.StartsWith("http://")) baseUrl = "https://" + baseUrl[7..];
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return baseUrl + rel;
    }

    private static LibFile FromDownload(string name, JsonNode d, bool cp, bool extract)
    {
        var path = (string?)d["path"] ?? Maven.ToPath(name);
        return new LibFile(name, Abs(path), (string?)d["url"] ?? ApiConfig.LibrariesBase + path,
            (string?)d["sha1"], (long?)d["size"] ?? 0, cp, extract);
    }
}
