using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

[Flags]
public enum VersionFilter { Release = 1, Snapshot = 2, Old = 4, All = Release | Snapshot | Old }

/// <summary>
/// Версии Minecraft: манифест Mojang, установка клиента/библиотек/ассетов, слияние inheritsFrom.
/// </summary>
public sealed class VersionService(IHttpClientFactory httpFactory, DownloadService downloads, ILogger<VersionService> log)
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string LibrariesUrl = "https://libraries.minecraft.net/";
    private const string ResourcesUrl = "https://resources.download.minecraft.net/";

    private VersionManifest? _manifest;

    public async Task<VersionManifest> GetManifestAsync(bool force = false, CancellationToken ct = default)
    {
        if (_manifest is not null && !force) return _manifest;
        var cache = Path.Combine(LauncherPaths.Cache, "version_manifest_v2.json");
        try
        {
            var http = httpFactory.CreateClient(DownloadService.HttpClientName);
            var json = await http.GetStringAsync(ManifestUrl, ct);
            Directory.CreateDirectory(LauncherPaths.Cache);
            await File.WriteAllTextAsync(cache, json, ct);
            _manifest = JsonSerializer.Deserialize<VersionManifest>(json, Json.Mojang);
        }
        catch (HttpRequestException ex) when (File.Exists(cache))
        {
            // Офлайн: работаем по сохранённому манифесту.
            log.LogWarning(ex, "Манифест недоступен, используем кэш");
            _manifest = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cache, ct), Json.Mojang);
        }

        return _manifest ?? throw new InvalidOperationException(L.T("Не удалось получить список версий"));
    }

    public async Task<IReadOnlyList<ManifestEntry>> ListAsync(VersionFilter filter, CancellationToken ct = default)
    {
        var m = await GetManifestAsync(ct: ct);
        return m.Versions.Where(v =>
            (filter.HasFlag(VersionFilter.Release) && v.IsRelease) ||
            (filter.HasFlag(VersionFilter.Snapshot) && v.IsSnapshot) ||
            (filter.HasFlag(VersionFilter.Old) && v.IsOld)).ToList();
    }

    /// <summary>Скачивает json ванильной версии (если ещё нет).</summary>
    public async Task<VersionInfo> EnsureVanillaJsonAsync(string id, CancellationToken ct)
    {
        var path = LauncherPaths.VersionJson(id);
        if (!File.Exists(path))
        {
            var entry = (await GetManifestAsync(ct: ct)).Versions.FirstOrDefault(v => v.Id == id)
                        ?? throw new InvalidOperationException(L.T($"Версия {id} не найдена в манифесте Mojang"));
            await downloads.DownloadFileAsync(new DownloadItem(entry.Url, path, entry.Sha1, 0), ct);
        }

        return await LoadJsonAsync(id, ct);
    }

    public async Task<VersionInfo> LoadJsonAsync(string id, CancellationToken ct)
    {
        await using var fs = File.OpenRead(LauncherPaths.VersionJson(id));
        return await JsonSerializer.DeserializeAsync<VersionInfo>(fs, Json.Mojang, ct)
               ?? throw new InvalidDataException(L.T($"Повреждён {id}.json"));
    }

    /// <summary>
    /// Загружает версию и сливает с родителями по inheritsFrom (Fabric/Forge → vanilla).
    /// Библиотеки ребёнка имеют приоритет (дедуп по group:artifact[:classifier]).
    /// </summary>
    public async Task<VersionInfo> ResolveAsync(string id, CancellationToken ct)
    {
        var v = File.Exists(LauncherPaths.VersionJson(id)) ? await LoadJsonAsync(id, ct) : await EnsureVanillaJsonAsync(id, ct);
        if (v.InheritsFrom is null) return v;

        var parent = await ResolveAsync(v.InheritsFrom, ct);
        var seen = new HashSet<string>();
        var libs = new List<Library>();
        foreach (var lib in v.Libraries.Concat(parent.Libraries))
            if (seen.Add(Maven.Key(lib.Name) + (lib.Natives is null ? "" : ":natives")))
                libs.Add(lib);

        return new VersionInfo
        {
            Id = v.Id,
            InheritsFrom = null,
            Jar = v.Jar ?? parent.Jar ?? parent.Id,
            Type = v.Type ?? parent.Type,
            MainClass = v.MainClass ?? parent.MainClass,
            MinecraftArguments = v.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = v.Arguments is null && parent.Arguments is null ? null : new Arguments
            {
                Game = (parent.Arguments?.Game ?? new()).Concat(v.Arguments?.Game ?? new()).ToList(),
                Jvm = (parent.Arguments?.Jvm ?? new()).Concat(v.Arguments?.Jvm ?? new()).ToList(),
            },
            Libraries = libs,
            AssetIndex = v.AssetIndex ?? parent.AssetIndex,
            Assets = v.Assets ?? parent.Assets,
            Downloads = v.Downloads ?? parent.Downloads,
            JavaVersion = v.JavaVersion ?? parent.JavaVersion,
            Logging = v.Logging ?? parent.Logging,
            ReleaseTime = parent.ReleaseTime ?? v.ReleaseTime,
        };
    }

    /// <summary>Полная установка: client.jar, библиотеки (+natives), ассеты, конфиг логирования.</summary>
    public async Task<VersionInfo> InstallAsync(string id, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(DownloadProgress.Indeterminate(L.T("Получаем информацию о версии")));
        var v = await ResolveAsync(id, ct);
        var jarId = v.Jar ?? v.Id;

        var items = new List<DownloadItem>();
        if (v.Downloads?.GetValueOrDefault("client") is { Url: not null } client)
            items.Add(new DownloadItem(client.Url, LauncherPaths.VersionJar(jarId), client.Sha1, client.Size));

        items.AddRange(LibraryDownloads(v));

        if (v.Logging?.Client?.File is { Url: not null } logCfg)
            items.Add(new DownloadItem(logCfg.Url, LoggingConfigPath(logCfg), logCfg.Sha1, logCfg.Size));

        await downloads.DownloadAsync(items, L.T("Клиент и библиотеки"), progress, ct);
        await InstallAssetsAsync(v, progress, ct);
        return v;
    }

    /// <summary>Сколько байт ещё не скачано для ванильной версии: client.jar, библиотеки и ассеты (индекс ассетов качается).</summary>
    public async Task<long> MissingBytesAsync(string id, CancellationToken ct)
    {
        var v = await EnsureVanillaJsonAsync(id, ct);
        var items = LibraryDownloads(v).ToList();
        if (v.Downloads?.GetValueOrDefault("client") is { Url: not null } client)
            items.Add(new DownloadItem(client.Url, LauncherPaths.VersionJar(v.Jar ?? v.Id), client.Sha1, client.Size));
        var bytes = items.DistinctBy(i => i.Path).Where(i => !File.Exists(i.Path)).Sum(i => i.Size);
        if (v.AssetIndex is { } ai)
        {
            var indexPath = Path.Combine(LauncherPaths.Assets, "indexes", ai.Id + ".json");
            if (!File.Exists(indexPath)) await downloads.DownloadFileAsync(new DownloadItem(ai.Url, indexPath, ai.Sha1, ai.Size), ct);
            var index = JsonSerializer.Deserialize<AssetIndexFile>(await File.ReadAllTextAsync(indexPath, ct), Json.Mojang)!;
            bytes += index.Objects.Values.DistinctBy(o => o.Hash)
                .Where(o => !File.Exists(Path.Combine(LauncherPaths.Assets, "objects", o.Hash[..2], o.Hash))).Sum(o => o.Size);
        }
        return bytes;
    }

    public static string LoggingConfigPath(DownloadInfo file) =>
        Path.Combine(LauncherPaths.Assets, "log_configs", file.Id ?? Path.GetFileName(file.Url!));

    /// <summary>Библиотеки, разрешённые правилами для Windows, включая natives-классификаторы.</summary>
    public static IEnumerable<DownloadItem> LibraryDownloads(VersionInfo v)
    {
        foreach (var lib in v.Libraries.Where(l => Rules.Allows(l.Rules) && l.Clientreq != false))
        {
            if (lib.Downloads?.Artifact is { } art)
            {
                var path = Path.Combine(LauncherPaths.Libraries, art.Path ?? Maven.ToPath(lib.Name));
                // Пустой url — библиотека создаётся процессорами Forge, качать нечего.
                if (!string.IsNullOrEmpty(art.Url))
                    yield return new DownloadItem(art.Url, path, art.Sha1, art.Size);
            }
            else if (lib.Downloads is null && lib.Natives is null)
            {
                // Fabric/Quilt/legacy Forge: только name + url репозитория.
                var rel = Maven.ToPath(lib.Name);
                var baseUrl = NormalizeMavenUrl(lib.Url) ?? LibrariesUrl;
                yield return new DownloadItem(baseUrl + rel, Path.Combine(LauncherPaths.Libraries, rel), lib.Sha1, lib.Size ?? 0);
            }

            if (NativeClassifier(lib) is { } classifier &&
                lib.Downloads?.Classifiers?.GetValueOrDefault(classifier) is { Url: not null } nat)
            {
                yield return new DownloadItem(nat.Url, Path.Combine(LauncherPaths.Libraries, nat.Path!), nat.Sha1, nat.Size);
            }
        }
    }

    /// <summary>Классификатор natives для Windows (старый формат: natives.windows = "natives-windows-${arch}").</summary>
    public static string? NativeClassifier(Library lib) =>
        lib.Natives?.GetValueOrDefault("windows")?.Replace("${arch}", Rules.ArchBits);

    private static string? NormalizeMavenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        // Старые Forge-профили ссылаются на мёртвый http-адрес.
        url = url.Replace("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/");
        return url.EndsWith('/') ? url : url + "/";
    }

    private async Task InstallAssetsAsync(VersionInfo v, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (v.AssetIndex is null) return;

        var indexPath = Path.Combine(LauncherPaths.Assets, "indexes", v.AssetIndex.Id + ".json");
        await downloads.DownloadFileAsync(new DownloadItem(v.AssetIndex.Url, indexPath, v.AssetIndex.Sha1, v.AssetIndex.Size), ct);
        var index = JsonSerializer.Deserialize<AssetIndexFile>(await File.ReadAllTextAsync(indexPath, ct), Json.Mojang)!;

        var items = index.Objects.Values
            .DistinctBy(o => o.Hash)
            .Select(o => new DownloadItem($"{ResourcesUrl}{o.Hash[..2]}/{o.Hash}",
                Path.Combine(LauncherPaths.Assets, "objects", o.Hash[..2], o.Hash), o.Hash, o.Size, QuickCheck: true))
            .ToList();
        await downloads.DownloadAsync(items, L.T("Ассеты"), progress, ct);

        // Очень старые версии (до 1.7) читают ассеты по именам, а не по хэшам.
        if (index.Virtual || index.MapToResources)
        {
            var target = Path.Combine(LauncherPaths.Assets, "virtual", v.AssetIndex.Id);
            foreach (var (name, obj) in index.Objects)
            {
                var dst = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(Path.Combine(LauncherPaths.Assets, "objects", obj.Hash[..2], obj.Hash), dst);
            }
        }
    }

    public static string? VirtualAssetsDir(VersionInfo v) => v.AssetIndex?.Id is "legacy" or "pre-1.6"
        ? Path.Combine(LauncherPaths.Assets, "virtual", v.AssetIndex.Id)
        : null;
}
