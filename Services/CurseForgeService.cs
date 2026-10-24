using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>CurseForge API v1. Требует бесплатный API-ключ (console.curseforge.com).</summary>
public sealed class CurseForgeService
{
    private readonly Func<string> _key;
    public CurseForgeService(Func<string> keyProvider) { _key = keyProvider; }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    /// <summary>Тип загрузчика в CurseForge: Forge=1, Fabric=4, Quilt=5, NeoForge=6.</summary>
    public static int? LoaderType(string loader) => loader switch
    { "Forge" => 1, "Fabric" => 4, "Quilt" => 5, "NeoForge" => 6, _ => null };

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var key = _key().Trim();
        if (key == "")
            throw new InvalidOperationException("Для CurseForge нужен бесплатный API-ключ: Настройки → «CurseForge API key» (получить: console.curseforge.com).");
        using var req = new HttpRequestMessage(method, ApiConfig.CurseForgeApi + path);
        req.Headers.Add("x-api-key", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("CurseForge отклонил API-ключ. Проверьте ключ в настройках.");
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(text)!;
    }

    public async Task<List<ModProject>> SearchAsync(string query, string mc, string loader, int offset, CancellationToken ct)
    {
        var path = $"/mods/search?gameId={ApiConfig.CurseForgeGameId}&classId={ApiConfig.CurseForgeModsClass}" +
                   $"&searchFilter={Esc(query ?? "")}&gameVersion={Esc(mc)}&sortField=2&sortOrder=desc&pageSize=20&index={offset}";
        if (LoaderType(loader) is int lt) path += $"&modLoaderType={lt}";
        var json = await SendAsync(HttpMethod.Get, path, null, ct);
        var res = new List<ModProject>();
        foreach (var m in json["data"]!.AsArray())
        {
            res.Add(new ModProject
            {
                Source = "curseforge",
                Id = ((long)m!["id"]!).ToString(),
                Name = (string?)m["name"] ?? "?",
                Summary = (string?)m["summary"] ?? "",
                IconUrl = (string?)m["logo"]?["thumbnailUrl"] ?? "",
                Author = (string?)m["authors"]?[0]?["name"] ?? "",
                Downloads = (long?)m["downloadCount"] ?? 0
            });
        }
        return res;
    }

    public async Task<ModFileInfo?> GetLatestFileAsync(string modId, string name, string mc, string loader, CancellationToken ct)
    {
        var path = $"/mods/{Esc(modId)}/files?gameVersion={Esc(mc)}&pageSize=50";
        if (LoaderType(loader) is int lt) path += $"&modLoaderType={lt}";
        var json = await SendAsync(HttpMethod.Get, path, null, ct);
        var files = json["data"]!.AsArray().Where(f => (bool?)f!["isAvailable"] != false)
            .OrderByDescending(f => DateTime.TryParse((string?)f!["fileDate"], out var d) ? d : DateTime.MinValue).ToList();
        if (files.Count == 0) return null;
        var pick = files.FirstOrDefault(f => (int?)f!["releaseType"] == 1) ?? files[0];
        return ToFile(pick!, modId, name);
    }

    /// <summary>Пакетное получение файлов по ID (для импорта сборок CurseForge).</summary>
    public async Task<List<ModFileInfo>> GetFilesByIdsAsync(IEnumerable<long> fileIds, CancellationToken ct)
    {
        var res = new List<ModFileInfo>();
        foreach (var chunk in fileIds.Chunk(50))
        {
            var body = new JsonObject { ["fileIds"] = new JsonArray(chunk.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) };
            var json = await SendAsync(HttpMethod.Post, "/mods/files", body, ct);
            foreach (var f in json["data"]!.AsArray())
                res.Add(ToFile(f!, ((long)f!["modId"]!).ToString(), (string?)f["displayName"] ?? "")!);
        }
        return res;
    }

    public async Task<string> GetModNameAsync(string modId, CancellationToken ct)
    {
        try { return (string?)(await SendAsync(HttpMethod.Get, $"/mods/{Esc(modId)}", null, ct))["data"]?["name"] ?? modId; }
        catch { return modId; }
    }

    private static ModFileInfo? ToFile(JsonNode f, string modId, string name)
    {
        long id = (long)f["id"]!;
        var fileName = (string)f["fileName"]!;
        var url = (string?)f["downloadUrl"];
        // автор запретил прямую ссылку — пробуем CDN по шаблону (может не сработать)
        if (string.IsNullOrEmpty(url)) url = string.Format(ApiConfig.CurseForgeEdge, id / 1000, id % 1000, Uri.EscapeDataString(fileName));
        var info = new ModFileInfo
        {
            Source = "curseforge", ProjectId = modId, FileId = id.ToString(), Name = name, FileName = fileName, Url = url!,
            Size = (long?)f["fileLength"] ?? 0
        };
        if (f["hashes"] is JsonArray hs)
            foreach (var h in hs)
                if ((int?)h!["algo"] == 1) info.Sha1 = (string?)h["value"]; // algo 1 = SHA1
        if (f["dependencies"] is JsonArray deps)
            foreach (var d in deps)
                if ((int?)d!["relationType"] == 3) info.Deps.Add(new ModDep(((long)d["modId"]!).ToString(), null)); // 3 = обязательная
        return info;
    }
}