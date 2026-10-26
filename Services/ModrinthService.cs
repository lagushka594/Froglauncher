using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Modrinth API v2 (ключ не нужен): поиск модов и получение совместимых версий.</summary>
public sealed class ModrinthService
{
    private static string Esc(string s) => Uri.EscapeDataString(s);

    public async Task<List<ModProject>> SearchAsync(string query, string mc, string? loader, int offset, CancellationToken ct)
    {
        var facets = $"[[\"project_type:mod\"],[\"versions:{mc}\"]" + (loader != null ? $",[\"categories:{loader}\"]" : "") + "]";
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url = $"{ApiConfig.ModrinthApi}/search?query={Esc(query ?? "")}&facets={Esc(facets)}&limit=20&offset={offset}&index={index}";
        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
        var res = new List<ModProject>();
        foreach (var h in json["hits"]!.AsArray())
        {
            res.Add(new ModProject
            {
                Source = "modrinth",
                Id = (string)h!["project_id"]!,
                Name = (string?)h["title"] ?? "?",
                Summary = (string?)h["description"] ?? "",
                IconUrl = (string?)h["icon_url"] ?? "",
                Author = (string?)h["author"] ?? "",
                Downloads = (long?)h["downloads"] ?? 0
            });
        }
        return res;
    }

    /// <summary>Свежая версия мода под игру и загрузчик (релизы предпочтительнее бета/альфа).</summary>
    public async Task<ModFileInfo?> GetLatestFileAsync(string projectId, string name, string mc, string? loader, CancellationToken ct)
    {
        var url = $"{ApiConfig.ModrinthApi}/project/{Esc(projectId)}/version?game_versions={Esc($"[\"{mc}\"]")}";
        if (loader != null) url += $"&loaders={Esc($"[\"{loader}\"]")}";
        var arr = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
        if (arr.Count == 0) return null;
        var pick = arr.FirstOrDefault(v => (string?)v!["version_type"] == "release") ?? arr[0];
        return ToFile(pick!, projectId, name);
    }

    public async Task<ModFileInfo?> GetVersionAsync(string versionId, string name, CancellationToken ct)
    {
        var v = JsonNode.Parse(await Http.GetStringAsync($"{ApiConfig.ModrinthApi}/version/{Esc(versionId)}", ct))!;
        return ToFile(v, (string)v["project_id"]!, name);
    }

    public async Task<string> GetProjectNameAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var p = JsonNode.Parse(await Http.GetStringAsync($"{ApiConfig.ModrinthApi}/project/{Esc(projectId)}", ct))!;
            return (string?)p["title"] ?? projectId;
        }
        catch { return projectId; }
    }

    private static ModFileInfo? ToFile(JsonNode version, string projectId, string name)
    {
        var files = version["files"]?.AsArray();
        if (files == null || files.Count == 0) return null;
        var f = files.FirstOrDefault(x => (bool?)x!["primary"] == true) ?? files[0];
        var info = new ModFileInfo
        {
            Source = "modrinth", ProjectId = projectId, FileId = (string?)version["id"] ?? "",
            Name = name, FileName = (string)f!["filename"]!, Url = (string)f["url"]!,
            Sha1 = (string?)f["hashes"]?["sha1"], Sha512 = (string?)f["hashes"]?["sha512"],
            Size = (long?)f["size"] ?? 0
        };
        if (version["dependencies"] is JsonArray deps)
            foreach (var d in deps)
                if ((string?)d!["dependency_type"] == "required" && (string?)d["project_id"] is string pid)
                    info.Deps.Add(new ModDep(pid, (string?)d["version_id"]));
        return info;
    }
}
