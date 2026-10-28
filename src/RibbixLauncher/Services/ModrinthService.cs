using RibbixLauncher.Core;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed record ModHit(string ProjectId, string Slug, string Title, string Description, string? IconUrl, long Downloads, string Author);

public sealed class ModsException(string message) : Exception(message);

/// <summary>
/// Каталог модов Modrinth (api.modrinth.com/v2): поиск под версию игры и модлоадер,
/// установка в папку mods с проверкой SHA1 и автоматической установкой обязательных зависимостей.
/// Доступен на любом тарифе, без ограничений.
/// </summary>
public sealed class ModrinthService(IHttpClientFactory httpFactory, DownloadService downloads, ILogger<ModrinthService> log)
{
    private const string Api = "https://api.modrinth.com/v2/";
    public const int PageSize = 20;

    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    /// <summary>Загрузчики Modrinth, моды которых подходят инстансу. Quilt запускает и моды Fabric.</summary>
    public static string[] Loaders(LoaderType type) => type switch
    {
        LoaderType.Fabric => new[] { "fabric" },
        LoaderType.Quilt => new[] { "quilt", "fabric" },
        LoaderType.Forge => new[] { "forge" },
        LoaderType.NeoForge => new[] { "neoforge" },
        _ => Array.Empty<string>(),
    };

    /// <summary>facets: внутри массива — ИЛИ, между массивами — И.</summary>
    internal static string Facets(Instance inst)
    {
        var loaders = string.Join(",", Loaders(inst.Loader).Select(l => $"\"categories:{l}\""));
        return $"[[\"project_type:mod\"],[\"versions:{inst.GameVersion}\"],[{loaders}]]";
    }

    public async Task<IReadOnlyList<ModHit>> SearchAsync(Instance inst, string? query, int offset, CancellationToken ct)
    {
        RequireLoader(inst);
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url = $"{Api}search?limit={PageSize}&offset={offset}&index={index}" +
                  $"&facets={Uri.EscapeDataString(Facets(inst))}" +
                  (string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim()));

        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct))!;
        return json["hits"]!.AsArray().Select(h => new ModHit(
            h!["project_id"]!.GetValue<string>(),
            h["slug"]?.GetValue<string>() ?? "",
            h["title"]?.GetValue<string>() ?? "?",
            h["description"]?.GetValue<string>() ?? "",
            h["icon_url"]?.GetValue<string>() is { Length: > 0 } icon ? icon : null,
            h["downloads"]?.GetValue<long>() ?? 0,
            h["author"]?.GetValue<string>() ?? "")).ToList();
    }

    /// <summary>Установить мод и его обязательные зависимости. Возвращает добавленные моды.</summary>
    public async Task<IReadOnlyList<InstalledMod>> InstallAsync(Instance inst, string projectId, CancellationToken ct)
    {
        RequireLoader(inst);
        var added = new List<InstalledMod>();
        await InstallRecursiveAsync(inst, projectId, isDependency: false, added, new HashSet<string>(), ct);
        return added;
    }

    private async Task InstallRecursiveAsync(Instance inst, string projectId, bool isDependency,
        List<InstalledMod> added, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(projectId) || inst.InstalledMods.Any(m => m.ProjectId == projectId)) return;

        var loaders = "[" + string.Join(",", Loaders(inst.Loader).Select(l => $"\"{l}\"")) + "]";
        var url = $"{Api}project/{projectId}/version?loaders={Uri.EscapeDataString(loaders)}" +
                  $"&game_versions={Uri.EscapeDataString($"[\"{inst.GameVersion}\"]")}";
        var versions = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();

        // API отдаёт версии от новых к старым; предпочитаем релиз, иначе берём что есть.
        var version = versions.FirstOrDefault(v => v!["version_type"]?.GetValue<string>() == "release") ?? versions.FirstOrDefault();
        if (version is null)
        {
            if (isDependency) { log.LogWarning("Зависимость {Project} недоступна для {Version}", projectId, inst.GameVersion); return; }
            throw new ModsException(L.T($"У этого мода нет сборки для {inst.GameVersion} · {inst.Loader}"));
        }

        var files = version["files"]!.AsArray();
        var file = files.FirstOrDefault(f => f!["primary"]?.GetValue<bool>() == true) ?? files.First();
        var fileName = Path.GetFileName(file!["filename"]!.GetValue<string>());
        var modsDir = Path.Combine(inst.GameDirectory, "mods");

        await downloads.DownloadFileAsync(new DownloadItem(
            file["url"]!.GetValue<string>(),
            Path.Combine(modsDir, fileName),
            file["hashes"]?["sha1"]?.GetValue<string>(),
            file["size"]?.GetValue<long>() ?? 0), ct);

        var project = JsonNode.Parse(await Http.GetStringAsync($"{Api}project/{projectId}", ct))!;
        var mod = new InstalledMod
        {
            ProjectId = projectId,
            VersionId = version["id"]!.GetValue<string>(),
            FileName = fileName,
            Title = project["title"]?.GetValue<string>() ?? fileName,
            IconUrl = project["icon_url"]?.GetValue<string>(),
            IsDependency = isDependency,
        };
        inst.InstalledMods.Add(mod);
        added.Add(mod);
        log.LogInformation("Мод {Title} ({File}) установлен в {Instance}", mod.Title, fileName, inst.Id);

        foreach (var dep in version["dependencies"]?.AsArray() ?? new JsonArray())
        {
            if (dep?["dependency_type"]?.GetValue<string>() != "required") continue;
            if (dep["project_id"]?.GetValue<string>() is { Length: > 0 } depId)
                await InstallRecursiveAsync(inst, depId, isDependency: true, added, visited, ct);
        }
    }

    /// <summary>
    /// Ресурспак, шейдер или датапак с Modrinth в версию: файл под её Minecraft (релиз, иначе что есть) —
    /// в resourcepacks, shaderpacks или saves/&lt;мир&gt;/datapacks. Возвращает имя файла.
    /// </summary>
    public async Task<string> InstallFileAsync(Instance inst, string projectId, string projectType, string? world, CancellationToken ct)
    {
        var folder = projectType switch
        {
            "resourcepack" => Path.Combine(inst.GameDirectory, "resourcepacks"),
            "shader" => Path.Combine(inst.GameDirectory, "shaderpacks"),
            "datapack" => world is { Length: > 0 } w ? Path.Combine(inst.GameDirectory, "saves", Path.GetFileName(w), "datapacks")
                : throw new ModsException(L.T("Выберите мир, в который положить датапак")),
            _ => throw new ModsException(L.T("Неизвестный тип файла")),
        };
        var url = $"{Api}project/{projectId}/version?game_versions={Uri.EscapeDataString($"[\"{inst.GameVersion}\"]")}" +
                  (projectType == "datapack" ? "&loaders=" + Uri.EscapeDataString("[\"datapack\"]") : "");
        var versions = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
        var version = versions.FirstOrDefault(v => v!["version_type"]?.GetValue<string>() == "release") ?? versions.FirstOrDefault()
                      ?? throw new ModsException(L.T($"Нет файла под Minecraft {inst.GameVersion}"));
        var files = version["files"]!.AsArray();
        var file = files.FirstOrDefault(f => f!["primary"]?.GetValue<bool>() == true) ?? files.First();
        var name = Path.GetFileName(file!["filename"]!.GetValue<string>());
        await downloads.DownloadFileAsync(new DownloadItem(file["url"]!.GetValue<string>(), Path.Combine(folder, name),
            file["hashes"]?["sha1"]?.GetValue<string>(), file["size"]?.GetValue<long>() ?? 0), ct);
        log.LogInformation("{Type} {File} → {Instance}", projectType, name, inst.Id);
        return name;
    }

    /// <summary>Удалить jar из папки mods (и запись о моде из каталога, если есть).</summary>
    public void Remove(Instance inst, string fileName)
    {
        var path = Path.Combine(inst.GameDirectory, "mods", Path.GetFileName(fileName));
        if (File.Exists(path)) File.Delete(path);
        var jar = fileName.EndsWith(".disabled", StringComparison.Ordinal) ? fileName[..^".disabled".Length] : fileName;
        inst.InstalledMods.RemoveAll(m => m.FileName == jar);
    }

    private static void RequireLoader(Instance inst)
    {
        if (inst.Loader == LoaderType.Vanilla)
            throw new ModsException(L.T("Для модов нужна версия с модлоадером: создайте версию с Fabric, Quilt, Forge или NeoForge."));
    }
}
