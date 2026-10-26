using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

public sealed record LoaderVersion(string Version, bool Recommended, string Label);

/// <summary>
/// Модлоадеры. Fabric/Quilt — готовый профиль из Meta API. Forge/NeoForge — установщик:
/// legacy (1.7.10–1.12.2, install_profile.versionInfo) или современный (процессоры).
/// Результат — version.json в versions/{id}, который запускается как обычная версия через inheritsFrom.
/// </summary>
public sealed class ModLoaderService(
    IHttpClientFactory httpFactory,
    VersionService versions,
    JavaService java,
    DownloadService downloads,
    ILogger<ModLoaderService> log)
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    private const string ForgeFiles = "https://files.minecraftforge.net/net/minecraftforge/forge/";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    private const string NeoForgeApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(LoaderType type, string gameVersion, CancellationToken ct)
    {
        switch (type)
        {
            case LoaderType.Fabric:
            case LoaderType.Quilt:
            {
                var url = type == LoaderType.Fabric
                    ? $"{FabricMeta}versions/loader/{gameVersion}"
                    : $"{QuiltMeta}versions/loader/{gameVersion}";
                var arr = JsonNode.Parse(await Http.GetStringAsync(url, ct))!.AsArray();
                return arr.Select(n =>
                {
                    var loader = n!["loader"]!;
                    var v = loader["version"]!.GetValue<string>();
                    var stable = loader["stable"]?.GetValue<bool>() ?? !v.Contains("beta");
                    return new LoaderVersion(v, stable, stable ? v : $"{v} (beta)");
                }).ToList();
            }

            case LoaderType.Forge:
            {
                var meta = JsonNode.Parse(await Http.GetStringAsync(ForgeFiles + "maven-metadata.json", ct))!;
                var promos = JsonNode.Parse(await Http.GetStringAsync(ForgeFiles + "promotions_slim.json", ct))!["promos"]!;
                var recommended = promos[$"{gameVersion}-recommended"]?.GetValue<string>();
                var list = meta[gameVersion]?.AsArray().Select(n => n!.GetValue<string>()).Reverse().ToList() ?? new();
                // Полная версия Forge: "1.20.1-47.2.0" или legacy "1.7.10-10.13.4.1614-1.7.10".
                return list.Select(full =>
                {
                    var shortV = full.Split('-')[1];
                    var rec = shortV == recommended;
                    return new LoaderVersion(full, rec, rec ? $"{shortV} ⭐ рекомендуемая" : shortV);
                }).ToList();
            }

            case LoaderType.NeoForge:
            {
                var json = JsonNode.Parse(await Http.GetStringAsync(NeoForgeApi, ct))!;
                var prefix = NeoForgePrefix(gameVersion);
                return json["versions"]!.AsArray()
                    .Select(n => n!.GetValue<string>())
                    .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                    .Reverse()
                    .Select(v => new LoaderVersion(v, !v.Contains("beta"), v))
                    .ToList();
            }

            default:
                return Array.Empty<LoaderVersion>();
        }
    }

    /// <summary>Устанавливает vanilla + модлоадер и возвращает ID версии для запуска.</summary>
    public async Task<string> InstallAsync(LoaderType type, string gameVersion, string? loaderVersion,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await versions.InstallAsync(gameVersion, progress, ct);
        if (type == LoaderType.Vanilla) return gameVersion;
        if (string.IsNullOrEmpty(loaderVersion))
            loaderVersion = (await GetVersionsAsync(type, gameVersion, ct)).FirstOrDefault(v => v.Recommended)?.Version
                            ?? throw new InvalidOperationException($"{type} не поддерживает {gameVersion}");

        progress?.Report(DownloadProgress.Indeterminate($"Устанавливаем {type} {loaderVersion}"));
        var id = type switch
        {
            LoaderType.Fabric => await InstallProfileJsonAsync($"{FabricMeta}versions/loader/{gameVersion}/{loaderVersion}/profile/json", ct),
            LoaderType.Quilt => await InstallProfileJsonAsync($"{QuiltMeta}versions/loader/{gameVersion}/{loaderVersion}/profile/json", ct),
            LoaderType.Forge => await InstallForgeLikeAsync(
                $"{ForgeMaven}{loaderVersion}/forge-{loaderVersion}-installer.jar", gameVersion, progress, ct),
            LoaderType.NeoForge => await InstallForgeLikeAsync(
                $"{NeoForgeMaven}{loaderVersion}/neoforge-{loaderVersion}-installer.jar", gameVersion, progress, ct),
            _ => gameVersion,
        };

        // Докачиваем библиотеки модлоадера.
        await versions.InstallAsync(id, progress, ct);
        log.LogInformation("Установлен {Loader} {LoaderVersion} для {Game} → {Id}", type, loaderVersion, gameVersion, id);
        return id;
    }

    /// <summary>NeoForge нумеруется по версии игры: 1.21.1 → 21.1.x, 1.21 → 21.0.x, 1.20.4 → 20.4.x.</summary>
    internal static string NeoForgePrefix(string gameVersion)
    {
        var p = gameVersion.Split('.');
        // С 2026 года версии игры по годам (26.2, 26.2.1) — NeoForge повторяет их как есть.
        if (p[0] != "1") return p.Length >= 2 ? $"{p[0]}.{p[1]}." : $"{p[0]}.0.";
        return p.Length >= 3 ? $"{p[1]}.{p[2]}." : $"{p[1]}.0.";
    }

    private async Task<string> InstallProfileJsonAsync(string url, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct);
        var id = JsonNode.Parse(json)!["id"]!.GetValue<string>();
        var path = LauncherPaths.VersionJson(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct);
        return id;
    }

    // ---------------- Forge / NeoForge ----------------

    private async Task<string> InstallForgeLikeAsync(string installerUrl, string gameVersion,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var installer = Path.Combine(LauncherPaths.Cache, "installers", Path.GetFileName(installerUrl));
        await downloads.DownloadFileAsync(new DownloadItem(installerUrl, installer, null, 0), ct);

        using var zip = ZipFile.OpenRead(installer);
        var profile = JsonNode.Parse(await ReadEntryAsync(zip, "install_profile.json", ct))!;

        return profile["versionInfo"] is { } legacyInfo
            ? await InstallLegacyForgeAsync(zip, profile, legacyInfo, gameVersion, ct)
            : await InstallModernForgeAsync(zip, installer, profile, gameVersion, progress, ct);
    }

    /// <summary>1.7.10–1.12.2: versionInfo — готовый version.json, universal-jar лежит внутри установщика.</summary>
    private static async Task<string> InstallLegacyForgeAsync(ZipArchive zip, JsonNode profile, JsonNode versionInfo,
        string gameVersion, CancellationToken ct)
    {
        var id = versionInfo["id"]!.GetValue<string>();
        // Старые профили не ссылаются на vanilla — без этого не скачаются client.jar и ассеты.
        versionInfo["inheritsFrom"] ??= gameVersion;
        versionInfo["jar"] ??= gameVersion;
        var install = profile["install"]!;
        var universalPath = Path.Combine(LauncherPaths.Libraries, Maven.ToPath(install["path"]!.GetValue<string>()));
        Directory.CreateDirectory(Path.GetDirectoryName(universalPath)!);
        zip.GetEntry(install["filePath"]!.GetValue<string>())!.ExtractToFile(universalPath, overwrite: true);

        // У самой forge-библиотеки нет url — она уже распакована; остальным ставим нормальный репозиторий.
        foreach (var lib in versionInfo["libraries"]!.AsArray())
        {
            var name = lib!["name"]!.GetValue<string>();
            if (name.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal)) lib.AsObject().Remove("url");
        }

        await WriteVersionJsonAsync(id, versionInfo.ToJsonString(), ct);
        return id;
    }

    /// <summary>1.12.2 (новые сборки) – 1.21+: version.json + библиотеки профиля + процессоры (side=client).</summary>
    private async Task<string> InstallModernForgeAsync(ZipArchive zip, string installerPath, JsonNode profile,
        string gameVersion, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var versionJson = await ReadEntryAsync(zip, (profile["json"]?.GetValue<string>() ?? "/version.json").TrimStart('/'), ct);
        var id = JsonNode.Parse(versionJson)!["id"]!.GetValue<string>();
        await WriteVersionJsonAsync(id, versionJson, ct);

        // Встроенный maven/ установщика (там сама forge-библиотека).
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("maven/", StringComparison.Ordinal) && e.Length > 0))
        {
            var dst = SafeCombine(LauncherPaths.Libraries, e.FullName["maven/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            e.ExtractToFile(dst, overwrite: true);
        }

        // Библиотеки, нужные процессорам.
        var profileLibs = profile["libraries"]?.Deserialize<List<Library>>(Json.Mojang) ?? new();
        await downloads.DownloadAsync(VersionService.LibraryDownloads(new VersionInfo { Libraries = profileLibs }).ToList(),
            "Библиотеки установщика", progress, ct);

        var processors = profile["processors"]?.AsArray() ?? new JsonArray();
        if (processors.Count == 0) return id;

        var vanilla = await versions.ResolveAsync(gameVersion, ct);
        var javaRt = await java.EnsureAsync(Math.Max(JavaService.RequiredMajor(vanilla), 8), progress, ct);
        var javaExe = javaRt.JavaExe.Replace("javaw.exe", "java.exe");

        var tmp = Directory.CreateTempSubdirectory("frog-forge-").FullName;
        try
        {
            var data = BuildDataMap(zip, profile, gameVersion, installerPath, tmp);
            var list = processors.Where(p => p!["sides"] is not JsonArray sides ||
                                             sides.Any(s => s!.GetValue<string>() == "client")).ToList();

            for (var i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DownloadProgress($"Forge: обработка {i + 1}/{list.Count}", i, list.Count, 0, 0, 0));
                await RunProcessorAsync(list[i]!, data, javaExe, ct);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }

        return id;
    }

    private static Dictionary<string, string> BuildDataMap(ZipArchive zip, JsonNode profile, string gameVersion,
        string installerPath, string tmp)
    {
        var data = new Dictionary<string, string>
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = LauncherPaths.VersionJar(gameVersion),
            ["MINECRAFT_VERSION"] = gameVersion,
            ["ROOT"] = LauncherPaths.Game,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = LauncherPaths.Libraries,
        };

        if (profile["data"] is JsonObject entries)
        {
            foreach (var (key, node) in entries)
            {
                var value = node?["client"]?.GetValue<string>();
                if (value is null) continue;

                if (value.StartsWith('[') && value.EndsWith(']'))
                    data[key] = Path.Combine(LauncherPaths.Libraries, Maven.ToPath(value[1..^1]));
                else if (value.StartsWith('\'') && value.EndsWith('\''))
                    data[key] = value[1..^1];
                else if (value.StartsWith('/'))
                {
                    // Файл внутри установщика (например /data/client.lzma) — распаковываем во временную папку.
                    var dst = SafeCombine(tmp, value.TrimStart('/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    zip.GetEntry(value.TrimStart('/'))!.ExtractToFile(dst, overwrite: true);
                    data[key] = dst;
                }
                else data[key] = value;
            }
        }

        return data;
    }

    private async Task RunProcessorAsync(JsonNode proc, Dictionary<string, string> data, string javaExe, CancellationToken ct)
    {
        string Lib(string coord) => Path.Combine(LauncherPaths.Libraries, Maven.ToPath(coord));
        string Resolve(string arg) =>
            arg.StartsWith('{') && arg.EndsWith('}') ? data.GetValueOrDefault(arg[1..^1], arg)
            : arg.StartsWith('[') && arg.EndsWith(']') ? Lib(arg[1..^1])
            : arg;

        // Выходы уже на месте и хэши совпадают — пропускаем (повторная установка быстрая).
        if (proc["outputs"] is JsonObject outputs && outputs.Count > 0)
        {
            var allOk = true;
            foreach (var (k, v) in outputs)
            {
                var file = Resolve(k);
                var sha = Resolve(v!.GetValue<string>()).Trim('\'');
                if (!File.Exists(file) || !string.Equals(await Hashing.Sha1FileAsync(file, ct), sha, StringComparison.OrdinalIgnoreCase))
                {
                    allOk = false;
                    break;
                }
            }
            if (allOk) return;
        }

        var jar = Lib(proc["jar"]!.GetValue<string>());
        var mainClass = ReadMainClass(jar) ?? throw new InvalidDataException($"Нет Main-Class в {Path.GetFileName(jar)}");
        var cp = new[] { jar }.Concat(proc["classpath"]?.AsArray().Select(c => Lib(c!.GetValue<string>())) ?? Array.Empty<string>());

        var psi = new ProcessStartInfo(javaExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = LauncherPaths.Game,
        };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(Path.PathSeparator, cp));
        psi.ArgumentList.Add(mainClass);
        foreach (var a in proc["args"]?.AsArray() ?? new JsonArray()) psi.ArgumentList.Add(Resolve(a!.GetValue<string>()));

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            log.LogError("Процессор Forge {Main} упал: {Err}", mainClass, (await stderr).Split('\n').TakeLast(20));
            throw new InvalidOperationException($"Установка Forge не удалась на шаге {mainClass} (код {p.ExitCode})");
        }
        _ = await stdout;
    }

    private static string? ReadMainClass(string jar)
    {
        using var z = ZipFile.OpenRead(jar);
        var entry = z.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null) return null;
        using var r = new StreamReader(entry.Open());
        while (r.ReadLine() is { } line)
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                return line["Main-Class:".Length..].Trim();
        return null;
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name, CancellationToken ct)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"В установщике нет {name}");
        using var r = new StreamReader(entry.Open());
        return await r.ReadToEndAsync(ct);
    }

    private static async Task WriteVersionJsonAsync(string id, string json, CancellationToken ct)
    {
        var path = LauncherPaths.VersionJson(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>Защита от zip-slip: путь не должен выходить за пределы root.</summary>
    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Недопустимый путь в архиве");
        return full;
    }
}
