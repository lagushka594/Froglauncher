using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RibbixLauncher.Services;

/// <summary>
/// Java: поиск установленных JRE/JDK (реестр + стандартные папки), загрузка Eclipse Temurin,
/// подбор версии под Minecraft: ≤1.16 → 8, 1.17–1.20.4 → 17, 1.20.5+ → 21.
/// </summary>
public sealed partial class JavaService(IHttpClientFactory httpFactory, DownloadService downloads, ILogger<JavaService> log)
{
    private static readonly string[] RegistryRoots =
    {
        @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\JavaSoft\JRE",
        @"SOFTWARE\JavaSoft\Java Development Kit",
        @"SOFTWARE\JavaSoft\JDK",
        @"SOFTWARE\Eclipse Adoptium\JRE",
        @"SOFTWARE\Eclipse Adoptium\JDK",
        @"SOFTWARE\Eclipse Foundation\JDK",
        @"SOFTWARE\Microsoft\JDK",
        @"SOFTWARE\Azul Systems\Zulu",
        @"SOFTWARE\BellSoft\Liberica",
    };

    private static readonly string[] RegistryValueNames = { "JavaHome", "Path", "InstallationPath" };

    private List<JavaRuntime>? _cache;

    public async Task<IReadOnlyList<JavaRuntime>> DetectAsync(bool force = false, CancellationToken ct = default)
    {
        if (_cache is not null && !force) return _cache;

        var homes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? home, string source)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            home = home.Trim().TrimEnd('\\');
            if (File.Exists(Path.Combine(home, "bin", "java.exe"))) homes.TryAdd(home, source);
        }

        foreach (var hive in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, hive);
            foreach (var root in RegistryRoots)
                foreach (var home in ScanRegistry(hklm.OpenSubKey(root), 0))
                    Add(home, L.T("реестр"));
        }

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");

        var pf = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto" })
            foreach (var root in pf.Where(p => !string.IsNullOrEmpty(p)))
                foreach (var dir in SafeDirs(Path.Combine(root, vendor)))
                    Add(dir, vendor);

        // Свои скачанные рантаймы и рантаймы официального лаунчера.
        foreach (var dir in SafeDirs(LauncherPaths.Runtimes)) Add(FindHome(dir), "RibbixLauncher");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var dir in SafeDirs(Path.Combine(appData, ".minecraft", "runtime"), recursive: true))
            Add(dir, "Minecraft Launcher");

        var result = new List<JavaRuntime>();
        foreach (var (home, source) in homes)
        {
            var exe = Path.Combine(home, "bin", File.Exists(Path.Combine(home, "bin", "javaw.exe")) ? "javaw.exe" : "java.exe");
            var version = ReadReleaseVersion(home) ?? await ProbeVersionAsync(Path.Combine(home, "bin", "java.exe"), ct);
            if (version is null) continue;
            result.Add(new JavaRuntime(exe, ParseMajor(version), version, source));
        }

        _cache = result.OrderByDescending(r => r.Major).ToList();
        log.LogInformation("Найдено Java: {Count}", _cache.Count);
        return _cache;
    }

    /// <summary>Нужная мажорная версия Java для версии игры.</summary>
    public static int RequiredMajor(VersionInfo v)
    {
        if (v.JavaVersion is { MajorVersion: > 0 } jv) return jv.MajorVersion;
        return RequiredMajor(v.Jar ?? v.Id);
    }

    public static int RequiredMajor(string gameVersion)
    {
        var m = ReleaseRegex().Match(gameVersion);
        if (!m.Success) return 21; // снапшоты без javaVersion — свежая Java
        int minor = int.Parse(m.Groups[1].Value), patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (minor <= 16) return 8;
        if (minor < 20 || (minor == 20 && patch <= 4)) return 17;
        return 21;
    }

    /// <summary>Подходящая Java: точное совпадение мажорной версии, иначе скачиваем Temurin.</summary>
    public async Task<JavaRuntime> EnsureAsync(int major, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var all = await DetectAsync(ct: ct);
        // Java 8 строго 8 (старый Forge ломается на 9+), для новых — та же мажорная версия.
        var match = all.FirstOrDefault(j => j.Major == major && j.Source == "RibbixLauncher")
                    ?? all.FirstOrDefault(j => j.Major == major);
        if (match is not null) return match;

        await DownloadTemurinAsync(major, progress, ct);
        return (await DetectAsync(force: true, ct)).First(j => j.Major == major && j.Source == "RibbixLauncher");
    }

    /// <summary>Скачивание JRE Eclipse Temurin с проверкой SHA-256 и распаковкой с прогрессом.</summary>
    public async Task DownloadTemurinAsync(int major, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(DownloadProgress.Indeterminate(L.T($"Ищем Java {major}")));
        var http = httpFactory.CreateClient(DownloadService.HttpClientName);
        var arch = Rules.IsArm64 ? "aarch64" : Rules.IsX86 ? "x32" : "x64";
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type=jre&os=windows&vendor=eclipse";

        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var pkg = doc.RootElement.EnumerateArray()
            .Select(a => a.GetProperty("binary").GetProperty("package"))
            .FirstOrDefault(p => p.GetProperty("name").GetString()!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (pkg.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(L.T($"Adoptium не отдал zip для Java {major}"));

        var zipPath = Path.Combine(LauncherPaths.Cache, pkg.GetProperty("name").GetString()!);
        await downloads.DownloadAsync(new[]
        {
            new DownloadItem(pkg.GetProperty("link").GetString()!, zipPath, null, pkg.GetProperty("size").GetInt64()),
        }, $"Java {major}", progress, ct);

        var expected = pkg.GetProperty("checksum").GetString();
        if (!string.Equals(await Hashing.Sha256FileAsync(zipPath, ct), expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new HashMismatchException(zipPath);
        }

        var target = Path.Combine(LauncherPaths.Runtimes, $"java-{major}");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var total = zip.Entries.Count;
            var rootFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.Entries[i];
                var dst = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!dst.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(dst);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    entry.ExtractToFile(dst, overwrite: true);
                }

                if (i % 50 == 0) progress?.Report(new DownloadProgress(L.T($"Распаковка Java {major}"), i, total, 0, 0, 0));
            }
        }, ct);

        File.Delete(zipPath);
        log.LogInformation("Java {Major} установлена в {Path}", major, target);
    }

    private static IEnumerable<string> ScanRegistry(RegistryKey? key, int depth)
    {
        if (key is null || depth > 4) yield break;
        using (key)
        {
            foreach (var name in RegistryValueNames)
                if (key.GetValue(name) is string s) yield return s;
            foreach (var sub in key.GetSubKeyNames())
                foreach (var v in ScanRegistry(key.OpenSubKey(sub), depth + 1))
                    yield return v;
        }
    }

    private static IEnumerable<string> SafeDirs(string root, bool recursive = false)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            if (!recursive) return Directory.GetDirectories(root);
            return Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories)
                .Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)!)!)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>В архиве Temurin JRE лежит в подпапке jdk-17.0.x+y-jre.</summary>
    private static string? FindHome(string dir) =>
        File.Exists(Path.Combine(dir, "bin", "java.exe"))
            ? dir
            : SafeDirs(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")));

    private static string? ReadReleaseVersion(string home)
    {
        var release = Path.Combine(home, "release");
        if (!File.Exists(release)) return null;
        foreach (var line in File.ReadLines(release))
            if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                return line["JAVA_VERSION=".Length..].Trim('"');
        return null;
    }

    private static async Task<string?> ProbeVersionAsync(string javaExe, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var text = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var m = VersionOutputRegex().Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"1.8.0_392" → 8, "17.0.9" → 17.</summary>
    public static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        var first = int.TryParse(parts[0], out var a) ? a : 0;
        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : first;
    }

    [GeneratedRegex(@"^1\.(\d+)(?:\.(\d+))?")]
    private static partial Regex ReleaseRegex();

    [GeneratedRegex("version \"([^\"]+)\"")]
    private static partial Regex VersionOutputRegex();
}
