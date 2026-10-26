using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Манифест Mojang, установка клиента, библиотек и ассетов (с проверкой SHA1).</summary>
public sealed class VersionService
{
    private List<McVersion>? _cache;

    public async Task<List<McVersion>> GetVersionsAsync(bool force, CancellationToken ct)
    {
        if (_cache != null && !force) return _cache;
        var json = JsonNode.Parse(await Http.GetStringAsync(ApiConfig.VersionManifest, ct))!;
        var list = new List<McVersion>();
        foreach (var v in json["versions"]!.AsArray())
        {
            list.Add(new McVersion
            {
                Id = (string)v!["id"]!,
                Type = (string)v["type"]!,
                Url = (string)v["url"]!,
                Sha1 = (string?)v["sha1"] ?? "",
                ReleaseTime = DateTime.TryParse((string?)v["releaseTime"], out var d) ? d : DateTime.MinValue
            });
        }
        return _cache = list;
    }

    /// <summary>Скачивает version.json ванильной версии (если его ещё нет).</summary>
    public async Task EnsureVanillaJsonAsync(string id, CancellationToken ct)
    {
        var path = VersionJson.JsonPath(id);
        if (File.Exists(path)) return;
        var v = (await GetVersionsAsync(false, ct)).FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException($"Версия «{id}» не найдена в манифесте Mojang.");
        await Downloader.DownloadAsync(new[] { new DownloadItem(v.Url, path, v.Sha1) }, null, ct);
    }

    public async Task<long> GetClientSizeAsync(McVersion v, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(v.Url, ct);
        var node = JsonNode.Parse(text)!;
        return (long?)node["downloads"]?["client"]?["size"] ?? 0;
    }

    /// <summary>
    /// Устанавливает всё необходимое для запуска версии (ванильной или с модлоадером):
    /// клиент, библиотеки, индекс и объекты ассетов. Уже скачанное и валидное пропускается.
    /// </summary>
    public async Task InstallAsync(string versionId, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var rootChain = versionId;
        // для ванили json может отсутствовать — докачиваем из манифеста
        if (!VersionJson.Exists(rootChain)) await EnsureVanillaJsonAsync(rootChain, ct);
        var first = VersionJson.Load(versionId);
        if (first["inheritsFrom"] is JsonNode p) await EnsureVanillaJsonAsync((string)p!, ct);

        var merged = VersionJson.LoadMerged(versionId);
        var items = new List<DownloadItem>();

        // клиентский jar
        var jarId = (string)merged["_jarId"]!;
        var client = merged["downloads"]?["client"];
        if (client != null)
            items.Add(new DownloadItem((string)client["url"]!, VersionJson.JarPath(jarId), (string?)client["sha1"], (long?)client["size"] ?? 0));
        else if (!File.Exists(VersionJson.JarPath(jarId)))
            throw new InvalidOperationException($"Нет клиента для {jarId}");

        // библиотеки
        foreach (var l in Libraries.Resolve(merged))
            items.Add(new DownloadItem(l.Url, l.AbsPath, l.Sha1, l.Size));

        // индекс ассетов
        var ai = merged["assetIndex"];
        string indexId = (string?)ai?["id"] ?? (string?)merged["assets"] ?? "legacy";
        string indexPath = Path.Combine(AppPaths.Assets, "indexes", indexId + ".json");
        if (ai != null)
            await Downloader.DownloadAsync(new[] { new DownloadItem((string)ai["url"]!, indexPath, (string?)ai["sha1"], (long?)ai["size"] ?? 0) }, null, ct);

        if (File.Exists(indexPath))
        {
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
            var objects = index["objects"]!.AsObject();
            foreach (var kv in objects)
            {
                var hash = (string)kv.Value!["hash"]!;
                var sub = hash[..2];
                items.Add(new DownloadItem(ApiConfig.ResourcesBase + sub + "/" + hash,
                    Path.Combine(AppPaths.Assets, "objects", sub, hash), hash, (long?)kv.Value["size"] ?? 0));
            }
        }

        Log.Info($"Установка {versionId}: файлов {items.Count}");
        await Downloader.DownloadAsync(items, progress, ct, "Minecraft " + versionId);

        // старые версии (≤1.6) требуют «виртуальных» ассетов с реальными именами файлов
        if (File.Exists(indexPath))
        {
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
            bool virt = (bool?)index["virtual"] == true || (bool?)index["map_to_resources"] == true;
            if (virt) await Task.Run(() => CopyVirtualAssets(index, indexId), ct);
        }
    }

    public static string VirtualAssetsDir(string indexId) => Path.Combine(AppPaths.Assets, "virtual", indexId);

    private static void CopyVirtualAssets(JsonNode index, string indexId)
    {
        var dst = VirtualAssetsDir(indexId);
        foreach (var kv in index["objects"]!.AsObject())
        {
            var hash = (string)kv.Value!["hash"]!;
            var src = Path.Combine(AppPaths.Assets, "objects", hash[..2], hash);
            var target = Path.Combine(dst, kv.Key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) || !File.Exists(src)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(src, target);
        }
    }
}
