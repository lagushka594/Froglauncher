using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Win32;

namespace FrogLauncher.Services;

/// <summary>Поиск установленных Java, автовыбор и скачивание Temurin (Adoptium).</summary>
public sealed class JavaService
{
    /// <summary>Требуемая мажорная версия: из version.json (javaVersion), иначе по правилам из ТЗ.</summary>
    public static int RequiredMajor(string mcVersion, int? fromJson)
    {
        if (fromJson is int j && j > 0) return j;
        var m = Regex.Match(mcVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return 17; // снапшоты и т.п. — берём современную
        int minor = int.Parse(m.Groups[1].Value);
        int patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (minor <= 16) return 8;
        if (minor < 20 || (minor == 20 && patch <= 4)) return 17;
        return 21;
    }

    public Task<List<JavaInfo>> DetectAsync() => Task.Run(() =>
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddHome(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            try
            {
                h = Path.GetFullPath(h.Trim().Trim('"'));
                if (File.Exists(Path.Combine(h, "bin", "java.exe"))) homes.Add(h.TrimEnd('\\'));
            }
            catch { }
        }

        // 1) реестр Windows (оба представления: 64 и 32 бит)
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var root in new[]
                {
                    @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit",
                    @"SOFTWARE\JavaSoft\JRE", @"SOFTWARE\JavaSoft\JDK",
                    @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Adoptium\JRE",
                    @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\Azul Systems\Zulu", @"SOFTWARE\BellSoft\Liberica"
                })
                {
                    using var k = hklm.OpenSubKey(root);
                    if (k != null) WalkRegistry(k, AddHome);
                }
            }
            catch (Exception ex) { Log.Warn("Реестр Java: " + ex.Message); }
        }

        // 2) стандартные папки
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bases = new List<string>();
        foreach (var p in new[] { pf, pf86 })
            foreach (var n in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "AdoptOpenJDK", "Semeru", "Eclipse Foundation" })
                bases.Add(Path.Combine(p, n));
        bases.Add(Path.Combine(local, "Programs", "Eclipse Adoptium"));
        bases.Add(AppPaths.JavaRuntimes);
        bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"));
        foreach (var b in bases.Where(Directory.Exists))
        {
            try
            {
                foreach (var d1 in Directory.GetDirectories(b))
                {
                    AddHome(d1);
                    foreach (var d2 in Directory.GetDirectories(d1)) // runtime\java-runtime-gamma\windows-x64\...
                    {
                        AddHome(d2);
                        foreach (var d3 in Directory.GetDirectories(d2)) AddHome(d3);
                    }
                }
            }
            catch { }
        }

        // 3) JAVA_HOME и PATH
        AddHome(Environment.GetEnvironmentVariable("JAVA_HOME"));
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (p.TrimEnd('\\').EndsWith("bin", StringComparison.OrdinalIgnoreCase))
                AddHome(Path.GetDirectoryName(p.TrimEnd('\\')));

        var result = new List<JavaInfo>();
        foreach (var h in homes)
        {
            var info = ReadInfo(h);
            if (info != null) result.Add(info);
        }
        return result.OrderByDescending(j => j.Major).ThenBy(j => j.Home).ToList();
    });

    private static void WalkRegistry(RegistryKey key, Action<string?> add)
    {
        foreach (var name in key.GetValueNames())
            if (name is "JavaHome" or "Path") add(key.GetValue(name) as string);
        foreach (var sub in key.GetSubKeyNames())
        {
            try { using var s = key.OpenSubKey(sub); if (s != null) WalkRegistry(s, add); } catch { }
        }
    }

    /// <summary>Читает версию/вендор из файла release, при отсутствии — из вывода java -version.</summary>
    private static JavaInfo? ReadInfo(string home)
    {
        try
        {
            string version = "", vendor = "";
            var rel = Path.Combine(home, "release");
            if (File.Exists(rel))
            {
                foreach (var line in File.ReadAllLines(rel))
                {
                    if (line.StartsWith("JAVA_VERSION=")) version = line[13..].Trim('"');
                    else if (line.StartsWith("IMPLEMENTOR=")) vendor = line[12..].Trim('"');
                }
            }
            if (version == "")
            {
                var psi = new ProcessStartInfo(Path.Combine(home, "bin", "java.exe"), "-version")
                { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                var m = Regex.Match(err, "version \"([^\"]+)\"");
                if (!m.Success) return null;
                version = m.Groups[1].Value;
            }
            if (vendor == "") vendor = "Unknown";
            return new JavaInfo { Home = home, Version = version, Vendor = vendor, Major = ParseMajor(version) };
        }
        catch (Exception ex) { Log.Warn($"Java {home}: {ex.Message}"); return null; }
    }

    private static int ParseMajor(string v)
    {
        var parts = v.Split('.', '_', '-', '+');
        if (parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var m8)) return m8;
        return int.TryParse(parts[0], out var m) ? m : 0;
    }

    public static void MarkCompat(IEnumerable<JavaInfo> list, int required)
    {
        foreach (var j in list) j.Compatible = IsCompatible(j.Major, required);
    }

    // Java 8 нужна строго для старых версий; для 17+ подходит любая не ниже требуемой.
    private static bool IsCompatible(int have, int required) => required <= 8 ? have == 8 : have >= required;

    public JavaInfo? Pick(IEnumerable<JavaInfo> all, int required, string? manualPath)
    {
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            var home = manualPath.Trim().Trim('"');
            if (home.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)) home = Path.GetDirectoryName(Path.GetDirectoryName(home))!;
            var info = ReadInfo(home);
            if (info != null) return info;
            Log.Warn("Указанная вручную Java не найдена: " + manualPath);
        }
        var ok = all.Where(j => IsCompatible(j.Major, required)).ToList();
        return ok.FirstOrDefault(j => j.Major == required) ?? ok.OrderBy(j => j.Major).FirstOrDefault();
    }

    /// <summary>Скачивает JRE Temurin нужной версии в %AppData%\.froglauncher\java\jre{N} и распаковывает.</summary>
    public async Task<JavaInfo> DownloadAsync(int major, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var target = Path.Combine(AppPaths.JavaRuntimes, "jre" + major);
        var zip = Path.Combine(AppPaths.Cache, $"temurin-jre-{major}.zip");
        var url = string.Format(ApiConfig.AdoptiumJre, major);

        Log.Info("Скачивание Java " + major);
        if (File.Exists(zip)) File.Delete(zip);
        await Downloader.DownloadAsync(new[] { new DownloadItem(url, zip) }, progress, ct, $"Java {major}");

        progress?.Report(new DownloadProgress { Stage = $"Распаковка Java {major}…", FilesTotal = 1, FilesDone = 0 });
        var tmp = Path.Combine(AppPaths.Cache, "java-extract-" + major);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zip);
            int total = archive.Entries.Count, i = 0;
            foreach (var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                if (!dest.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase)) continue; // защита от zip-slip
                if (string.IsNullOrEmpty(e.Name)) Directory.CreateDirectory(dest);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                }
                if (++i % 50 == 0)
                    progress?.Report(new DownloadProgress { Stage = $"Распаковка Java {major}…", BytesDone = i, BytesTotal = total });
            }
        }, ct);

        var top = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(AppPaths.JavaRuntimes);
        Directory.Move(top, target);
        try { Directory.Delete(tmp, true); File.Delete(zip); } catch { }

        return ReadInfo(target) ?? throw new InvalidOperationException("Java скачана, но не распознана: " + target);
    }
}
