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

    /// <summary>Категории Modrinth, которые на самом деле загрузчики или служебные — по ним «похожесть» не ищем.</summary>
    private static readonly HashSet<string> NotGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "quilt", "forge", "neoforge", "liteloader", "modloader", "rift", "library", "minecraft",
    };

    /// <summary>Самые частые жанры среди модов (без загрузчиков и библиотек), не больше <paramref name="take"/>.</summary>
    internal static List<string> TopGenres(IEnumerable<IEnumerable<string>> categories, int take = 3) =>
        categories.SelectMany(c => c.Distinct()).Where(c => !NotGenres.Contains(c))
            .GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(take).Select(g => g.Key).ToList();

    /// <summary>
    /// «С этим часто ставят»: жанры уже стоящих модов → популярные моды тех же жанров под эту версию,
    /// кроме уже установленных. Отдельного API рекомендаций у Modrinth нет, поэтому так.
    /// </summary>
    public async Task<IReadOnlyList<ModHit>> SimilarAsync(Instance inst, CancellationToken ct)
    {
        RequireLoader(inst);
        var ids = inst.InstalledMods.Where(m => !m.IsDependency && m.ProjectId.Length > 0).Select(m => m.ProjectId).Distinct().Take(100).ToList();
        if (ids.Count == 0) throw new ModsException(L.T("Поставьте пару модов из каталога — по ним подберём похожие"));
        var url = $"{Api}projects?ids={Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(ids))}";
        var projects = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
        var genres = TopGenres(projects.Select(p => (p!["categories"]?.AsArray() ?? new JsonArray()).Select(c => c!.GetValue<string>())));
        if (genres.Count == 0) throw new ModsException(L.T("Не получилось понять, какие моды вам нравятся — попробуйте поиск"));

        var loaders = string.Join(",", Loaders(inst.Loader).Select(l => $"\"categories:{l}\""));
        var genreFacet = string.Join(",", genres.Select(g => $"\"categories:{g}\""));
        var facets = $"[[\"project_type:mod\"],[\"versions:{inst.GameVersion}\"],[{loaders}],[{genreFacet}]]";
        var search = $"{Api}search?limit=40&index=downloads&facets={Uri.EscapeDataString(facets)}";
        var installed = inst.InstalledMods.Select(m => m.ProjectId).ToHashSet();
        var json = JsonNode.Parse(await Http.GetStringAsync(search, ct))!;
        return json["hits"]!.AsArray()
            .Where(h => !installed.Contains(h!["project_id"]!.GetValue<string>()))
            .Select(h => new ModHit(
                h!["project_id"]!.GetValue<string>(),
                h["slug"]?.GetValue<string>() ?? "",
                h["title"]?.GetValue<string>() ?? "?",
                h["description"]?.GetValue<string>() ?? "",
                h["icon_url"]?.GetValue<string>() is { Length: > 0 } icon ? icon : null,
                h["downloads"]?.GetValue<long>() ?? 0,
                h["author"]?.GetValue<string>() ?? "")).Take(PageSize).ToList();
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

        var version = await PickVersionAsync(inst, projectId, ct);
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

    /// <summary>Версия мода под Minecraft и загрузчик версии: API отдаёт от новых к старым — предпочитаем релиз, иначе что есть.</summary>
    private async Task<JsonNode?> PickVersionAsync(Instance inst, string projectId, CancellationToken ct)
    {
        var loaders = "[" + string.Join(",", Loaders(inst.Loader).Select(l => $"\"{l}\"")) + "]";
        var url = $"{Api}project/{projectId}/version?loaders={Uri.EscapeDataString(loaders)}" +
                  $"&game_versions={Uri.EscapeDataString($"[\"{inst.GameVersion}\"]")}";
        var versions = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
        return versions.FirstOrDefault(v => v!["version_type"]?.GetValue<string>() == "release") ?? versions.FirstOrDefault();
    }

    /// <summary>Моды, которые сами по себе — движок отрисовки: два сразу не уживаются.</summary>
    internal static readonly string[] RenderEngines = { "sodium", "embeddium", "rubidium", "optifine", "optifabric" };

    /// <summary>ID проектов и версий, помеченных в зависимостях как incompatible.</summary>
    internal static HashSet<string> Incompatible(JsonNode? version) =>
        (version?["dependencies"]?.AsArray() ?? new JsonArray())
            .Where(d => d?["dependency_type"]?.GetValue<string>() == "incompatible")
            .SelectMany(d => new[] { d!["project_id"]?.GetValue<string>(), d["version_id"]?.GetValue<string>() })
            .OfType<string>().Where(id => id.Length > 0).ToHashSet();

    /// <summary>
    /// С чем конфликтует новый мод: его версия объявляет установленный несовместимым, установленный — его,
    /// или это второй движок отрисовки (Sodium, Embeddium, OptiFine…) рядом с уже стоящим. Возвращает названия.
    /// </summary>
    internal static List<string> FindConflicts(string projectId, string slug, ISet<string> incompatible,
        IEnumerable<(InstalledMod Mod, ISet<string> Incompatible)> installed, IEnumerable<string> jarNames)
    {
        var found = new List<string>();
        foreach (var (mod, theirs) in installed)
        {
            if (mod.ProjectId == projectId) continue;
            if (incompatible.Contains(mod.ProjectId) || incompatible.Contains(mod.VersionId) || theirs.Contains(projectId))
                found.Add(mod.Title);
        }
        var engine = RenderEngines.FirstOrDefault(e => slug.Equals(e, StringComparison.OrdinalIgnoreCase));
        if (engine is not null)
            foreach (var jar in jarNames.Select(j => j.ToLowerInvariant()))
                if (RenderEngines.FirstOrDefault(e => e != engine && jar.StartsWith(e)) is { } other)
                    found.Add(other is "optifine" or "optifabric" ? "OptiFine" : char.ToUpperInvariant(other[0]) + other[1..]);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Проверка перед установкой: с какими модами версии новый мод не уживётся (по данным Modrinth).</summary>
    public async Task<IReadOnlyList<string>> ConflictsAsync(Instance inst, string projectId, CancellationToken ct)
    {
        RequireLoader(inst);
        var version = await PickVersionAsync(inst, projectId, ct);
        if (version is null) return Array.Empty<string>();
        var slug = JsonNode.Parse(await Http.GetStringAsync($"{Api}project/{projectId}", ct))!["slug"]?.GetValue<string>() ?? "";

        var mods = inst.InstalledMods.Where(m => m.VersionId.Length > 0).ToList();
        var theirs = new Dictionary<string, HashSet<string>>();
        foreach (var chunk in mods.Select(m => m.VersionId).Distinct().Chunk(100))
        {
            var url = $"{Api}versions?ids={Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(chunk))}";
            foreach (var v in JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray())
                if (v?["id"]?.GetValue<string>() is { } id) theirs[id] = Incompatible(v);
        }
        var dir = Path.Combine(inst.GameDirectory, "mods");
        var jars = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jar").Select(Path.GetFileName).OfType<string>() : Enumerable.Empty<string>();
        return FindConflicts(projectId, slug, Incompatible(version),
            mods.Select(m => (m, (ISet<string>)(theirs.GetValueOrDefault(m.VersionId) ?? new HashSet<string>()))), jars);
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
