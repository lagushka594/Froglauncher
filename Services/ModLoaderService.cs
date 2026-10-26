using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Fabric, Quilt, Forge (старые и новые) и NeoForge. Возвращает id версии для запуска.</summary>
public sealed class ModLoaderService
{
    public static readonly string[] Loaders = { "Vanilla", "Fabric", "Forge", "Quilt", "NeoForge" };

    // ---------- Список версий загрузчика ----------

    public async Task<List<string>> GetLoaderVersionsAsync(string loader, string mcVersion, CancellationToken ct)
    {
        switch (loader)
        {
            case "Fabric": return await MetaVersionsAsync(ApiConfig.FabricLoaders, mcVersion, ct);
            case "Quilt":  return await MetaVersionsAsync(ApiConfig.QuiltLoaders, mcVersion, ct);
            case "Forge":
            {
                var all = await MavenVersionsAsync(ApiConfig.ForgeMetadata, ct);
                return all.Where(v => v.StartsWith(mcVersion + "-")).Reverse().ToList();
            }
            case "NeoForge":
            {
                var parts = mcVersion.Split('.');
                if (parts.Length < 2 || parts[0] != "1") return new();
                var prefix = $"{parts[1]}.{(parts.Length > 2 ? parts[2] : "0")}.";
                var all = await MavenVersionsAsync(ApiConfig.NeoMetadata, ct);
                return all.Where(v => v.StartsWith(prefix)).Reverse().ToList();
            }
            default: return new();
        }
    }

    // GET https://meta.fabricmc.net/v2/versions/loader/{game_version} → [{loader:{version,stable}}]
    private static async Task<List<string>> MetaVersionsAsync(string baseUrl, string mc, CancellationToken ct)
    {
        var arr = JsonNode.Parse(await Http.GetStringAsync(baseUrl + Uri.EscapeDataString(mc), ct))!.AsArray();
        return arr.Select(x => (string)x!["loader"]!["version"]!).ToList();
    }

    private static async Task<List<string>> MavenVersionsAsync(string url, CancellationToken ct)
    {
        var doc = XDocument.Parse(await Http.GetStringAsync(url, ct));
        return doc.Descendants("version").Select(x => x.Value).ToList();
    }

    // ---------- Установка ----------

    /// <param name="vanillaId">Ванильная версия уже должна быть установлена.</param>
    /// <param name="java">Нужна для запуска инсталляторов Forge/NeoForge нового формата.</param>
    public async Task<string> InstallAsync(string loader, string vanillaId, string loaderVersion, JavaInfo? java,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion)) throw new InvalidOperationException("Не выбрана версия загрузчика.");
        return loader switch
        {
            "Fabric" => await InstallMetaAsync(ApiConfig.FabricLoaders, vanillaId, loaderVersion, ct),
            "Quilt" => await InstallMetaAsync(ApiConfig.QuiltLoaders, vanillaId, loaderVersion, ct),
            "Forge" => await InstallInstallerAsync(ApiConfig.ForgeMaven, loaderVersion, "forge", java, progress, ct),
            "NeoForge" => await InstallInstallerAsync(ApiConfig.NeoMaven, loaderVersion, "neoforge", java, progress, ct),
            _ => vanillaId
        };
    }

    // Fabric/Quilt: готовый profile json лежит на meta-сервере, поверх ванили (inheritsFrom).
    private static async Task<string> InstallMetaAsync(string baseUrl, string mc, string loaderVer, CancellationToken ct)
    {
        var url = $"{baseUrl}{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loaderVer)}/profile/json";
        var text = await Http.GetStringAsync(url, ct);
        var json = JsonNode.Parse(text)!.AsObject();
        var id = (string)json["id"]!;
        Directory.CreateDirectory(VersionJson.Dir(id));
        await File.WriteAllTextAsync(VersionJson.JsonPath(id), json.ToJsonString(), ct);
        Log.Info("Установлен профиль " + id);
        return id;
    }

    private static async Task<string> InstallInstallerAsync(string mavenBase, string ver, string artifact, JavaInfo? java,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var installerUrl = $"{mavenBase}{ver}/{artifact}-{ver}-installer.jar";
        var installer = Path.Combine(AppPaths.Cache, $"{artifact}-{ver}-installer.jar");
        await Downloader.DownloadAsync(new[] { new DownloadItem(installerUrl, installer) }, progress, ct, $"{artifact} {ver}");

        // читаем install_profile.json: старый формат содержит versionInfo, новый — processors/spec
        JsonObject profile;
        using (var zip = ZipFile.OpenRead(installer))
        {
            var e = zip.GetEntry("install_profile.json") ?? throw new InvalidDataException("В инсталлере нет install_profile.json");
            using var sr = new StreamReader(e.Open());
            profile = JsonNode.Parse(sr.ReadToEnd())!.AsObject();
        }

        if (profile["versionInfo"] is JsonObject vi)
            return InstallLegacyForge(installer, profile, vi);

        // Новый формат (1.13+/NeoForge): запускаем инсталлятор в тихом режиме
        if (java == null) throw new InvalidOperationException("Для установки нужна Java.");
        var root = AppPaths.GameRoot;
        Directory.CreateDirectory(root);
        var lp = Path.Combine(root, "launcher_profiles.json");
        if (!File.Exists(lp)) File.WriteAllText(lp, "{\"profiles\":{},\"version\":3}");

        var before = Directory.Exists(AppPaths.Versions)
            ? Directory.GetDirectories(AppPaths.Versions).Select(Path.GetFileName).ToHashSet() : new HashSet<string?>();

        progress?.Report(new DownloadProgress { Stage = $"Установка {artifact} {ver} (может занять несколько минут)…" });
        var psi = new ProcessStartInfo(java.Exe)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-jar", installer, "--installClient", root }) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить инсталлятор.");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(ct);
        Log.Info("Инсталлятор вывод:\n" + output);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Инсталлятор завершился с кодом {p.ExitCode}. Подробности в logs/launcher.log.");

        var created = Directory.GetDirectories(AppPaths.Versions).Select(Path.GetFileName)
            .Where(n => n != null && !before.Contains(n)).ToList();
        var id = (string?)profile["version"] is string pv && Directory.Exists(Path.Combine(AppPaths.Versions, pv)) ? pv
               : created.FirstOrDefault(n => n!.Contains(artifact, StringComparison.OrdinalIgnoreCase)) ?? created.FirstOrDefault()
               ?? throw new InvalidOperationException("Инсталлятор не создал версию.");
        return id!;
    }

    // Старый Forge (1.5–1.12): «ручная распаковка» — берём versionInfo и universal.jar из инсталлера.
    private static string InstallLegacyForge(string installer, JsonObject profile, JsonObject versionInfo)
    {
        var id = (string)versionInfo["id"]!;
        Directory.CreateDirectory(VersionJson.Dir(id));
        File.WriteAllText(VersionJson.JsonPath(id), versionInfo.ToJsonString());

        var install = profile["install"]!.AsObject();
        var filePath = (string)install["filePath"]!;
        var mavenName = (string)install["path"]!;
        var dest = Path.Combine(AppPaths.Libraries, Maven.ToPath(mavenName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var zip = ZipFile.OpenRead(installer);
        var entry = zip.GetEntry(filePath) ?? throw new InvalidDataException("В инсталлере нет " + filePath);
        entry.ExtractToFile(dest, true);
        Log.Info("Forge (legacy) распакован: " + id);
        return id;
    }
}
