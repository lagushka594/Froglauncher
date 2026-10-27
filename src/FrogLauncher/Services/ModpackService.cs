using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

public sealed record ModpackHit(
    string ProjectId, string Slug, string Title, string Description, string? IconUrl,
    long Downloads, string Author, IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders);

/// <summary>modrinth.index.json внутри .mrpack (формат Modrinth, formatVersion 1).</summary>
public sealed class MrpackIndex
{
    public int FormatVersion { get; set; }
    public string Game { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<MrpackFile> Files { get; set; } = new();
    public Dictionary<string, string> Dependencies { get; set; } = new();
}

public sealed class MrpackFile
{
    public string Path { get; set; } = "";
    public Dictionary<string, string> Hashes { get; set; } = new();
    public Dictionary<string, string>? Env { get; set; }
    public List<string> Downloads { get; set; } = new();
    public long FileSize { get; set; }
}

/// <summary>
/// Сборки (модпаки) Modrinth: поиск и установка .mrpack в новую версию (инстанс).
/// Установка: скачать .mrpack → прочитать modrinth.index.json → создать версию с нужными Minecraft и модлоадером →
/// скачать файлы (SHA1, только разрешённые хосты Modrinth) → распаковать overrides/ и client-overrides/.
/// </summary>
public sealed class ModpackService(
    IHttpClientFactory httpFactory,
    DownloadService downloads,
    InstanceService instances,
    ILogger<ModpackService> log)
{
    private const string Api = "https://api.modrinth.com/v2/";
    public const int PageSize = 20;

    /// <summary>Хосты, с которых формат .mrpack разрешает качать файлы (спецификация Modrinth).</summary>
    private static readonly string[] AllowedHosts =
        { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" };

    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    public async Task<IReadOnlyList<ModpackHit>> SearchAsync(string? query, LoaderType? loader, string? gameVersion, int offset, CancellationToken ct)
    {
        var facets = new List<string> { "[\"project_type:modpack\"]" };
        if (loader is { } l && ModrinthService.Loaders(l) is { Length: > 0 } names)
            facets.Add("[" + string.Join(",", names.Take(1).Select(n => $"\"categories:{n}\"")) + "]");
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");

        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url = $"{Api}search?limit={PageSize}&offset={offset}&index={index}" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}" +
                  (string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim()));

        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
        string[] loaderNames = { "fabric", "forge", "neoforge", "quilt" };
        return json["hits"]!.AsArray().Select(h => new ModpackHit(
            h!["project_id"]!.GetValue<string>(),
            h["slug"]?.GetValue<string>() ?? "",
            h["title"]?.GetValue<string>() ?? "?",
            h["description"]?.GetValue<string>() ?? "",
            h["icon_url"]?.GetValue<string>() is { Length: > 0 } icon ? icon : null,
            h["downloads"]?.GetValue<long>() ?? 0,
            h["author"]?.GetValue<string>() ?? "",
            h["versions"]?.AsArray().Select(v => v!.GetValue<string>()).ToList() ?? new List<string>(),
            h["categories"]?.AsArray().Select(c => c!.GetValue<string>()).Where(loaderNames.Contains).ToList() ?? new List<string>()))
            .ToList();
    }

    /// <summary>Установить сборку в новую версию. Учитывает лимит версий тарифа (InstanceLimitException).</summary>
    public async Task<Instance> InstallAsync(ModpackHit pack, string? gameVersion, int ramMb, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // 1. Версия сборки: самая свежая (релиз приоритетнее), при фильтре — под выбранную версию игры.
        progress?.Report(DownloadProgress.Indeterminate(L.T("Ищем файл сборки")));
        var url = $"{Api}project/{pack.ProjectId}/version" +
                  (string.IsNullOrWhiteSpace(gameVersion) ? "" : "?game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        var versions = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
        var version = versions.FirstOrDefault(v => v!["version_type"]?.GetValue<string>() == "release") ?? versions.FirstOrDefault()
                      ?? throw new ModsException(L.T("У сборки нет файлов для установки") + (gameVersion is null ? "" : L.T($" под {gameVersion}")));
        var files = version["files"]!.AsArray();
        var file = files.FirstOrDefault(f => f!["filename"]?.GetValue<string>()?.EndsWith(".mrpack") == true) ?? files.First();

        var mrpackPath = Path.Combine(LauncherPaths.Cache, "modpacks", Path.GetFileName(file!["filename"]!.GetValue<string>()));
        await downloads.DownloadAsync(new[]
        {
            new DownloadItem(file["url"]!.GetValue<string>(), mrpackPath, file["hashes"]?["sha1"]?.GetValue<string>(), file["size"]?.GetValue<long>() ?? 0),
        }, L.T("Скачиваем сборку"), progress, ct);

        return await InstallFromFileAsync(mrpackPath, pack.Title, pack.IconUrl, ramMb, progress, ct);
    }

    /// <summary>Установить .mrpack с диска (импорт или скачанный из каталога) в новую версию.</summary>
    public async Task<Instance> InstallFromFileAsync(string mrpackPath, string? name, string? iconUrl, int ramMb,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var index = ReadIndex(zip);
        var (loader, loaderVersion, mc) = MapDependencies(index.Dependencies);
        var title = name ?? (index.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(mrpackPath));

        // 2. Новая версия (проверка лимита тарифа — здесь же).
        var inst = instances.Create(title, mc, loader, loaderVersion, ramMb);
        inst.IconUrl = iconUrl;
        try
        {
            // 3. Файлы сборки.
            var items = new List<DownloadItem>();
            foreach (var f in index.Files)
            {
                if (f.Env?.GetValueOrDefault("client") == "unsupported") continue; // только серверные
                var dst = SafePath(inst.GameDirectory, f.Path);
                var src = f.Downloads.FirstOrDefault(IsAllowedUrl)
                          ?? throw new ModsException(L.T($"Файл {f.Path} лежит на недоверенном хосте — установка остановлена"));
                items.Add(new DownloadItem(src, dst, f.Hashes.GetValueOrDefault("sha1"), f.FileSize));
            }
            await downloads.DownloadAsync(items, L.T("Моды сборки"), progress, ct);

            // 4. overrides/ — конфиги, ресурспаки, скрипты; client-overrides/ перекрывают общие.
            progress?.Report(DownloadProgress.Indeterminate(L.T("Распаковываем настройки сборки")));
            foreach (var prefix in new[] { "overrides/", "client-overrides/" })
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
                {
                    var dst = SafePath(inst.GameDirectory, e.FullName[prefix.Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst, overwrite: true);
                }

            instances.Save(inst);
            log.LogInformation("Сборка {Pack} {Version} установлена: {Mc} {Loader} {LoaderVersion}, файлов {Count}",
                title, index.VersionId, mc, loader, loaderVersion, items.Count);
            return inst;
        }
        catch
        {
            // Недокачанная сборка не должна занимать слот лимита версий.
            instances.Delete(inst);
            throw;
        }
    }

    internal static MrpackIndex ReadIndex(ZipArchive zip)
    {
        var entry = zip.GetEntry("modrinth.index.json") ?? throw new ModsException(L.T("Это не сборка Modrinth (.mrpack)"));
        using var s = entry.Open();
        var index = JsonSerializer.Deserialize<MrpackIndex>(s, Json.Mojang) ?? throw new ModsException(L.T("Повреждён modrinth.index.json"));
        if (index.Game != "minecraft") throw new ModsException(L.T("Сборка не для Minecraft"));
        return index;
    }

    /// <summary>dependencies: minecraft + fabric-loader | quilt-loader | forge | neoforge → тип модлоадера и его версия в формате лаунчера.</summary>
    internal static (LoaderType loader, string? loaderVersion, string mc) MapDependencies(IReadOnlyDictionary<string, string> deps)
    {
        var mc = deps.GetValueOrDefault("minecraft") ?? throw new ModsException(L.T("В сборке не указана версия Minecraft"));
        if (deps.TryGetValue("fabric-loader", out var fabric)) return (LoaderType.Fabric, fabric, mc);
        if (deps.TryGetValue("quilt-loader", out var quilt)) return (LoaderType.Quilt, quilt, mc);
        if (deps.TryGetValue("neoforge", out var neo)) return (LoaderType.NeoForge, neo, mc);
        // Версии Forge в лаунчере полные, как в maven: "1.20.1-47.2.0".
        if (deps.TryGetValue("forge", out var forge)) return (LoaderType.Forge, forge.StartsWith(mc + "-") ? forge : $"{mc}-{forge}", mc);
        return (LoaderType.Vanilla, null, mc);
    }

    /// <summary>Путь внутри папки игры; ../ и абсолютные пути из сборки запрещены.</summary>
    internal static string SafePath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(relative) || !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ModsException(L.T($"Недопустимый путь в сборке: {relative}"));
        return full;
    }

    internal static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
}
