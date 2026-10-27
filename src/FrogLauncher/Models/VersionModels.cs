using FrogLauncher.Core;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrogLauncher.Models;

// ---------- version_manifest_v2.json ----------

public sealed class VersionManifest
{
    public LatestInfo Latest { get; set; } = new();
    public List<ManifestEntry> Versions { get; set; } = new();
}

public sealed class LatestInfo
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

public sealed class ManifestEntry
{
    public string Id { get; set; } = "";
    /// <summary>release | snapshot | old_beta | old_alpha</summary>
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public DateTimeOffset ReleaseTime { get; set; }

    public bool IsRelease => Type == "release";
    public bool IsSnapshot => Type == "snapshot";
    public bool IsOld => Type is "old_beta" or "old_alpha";
}

// ---------- {version}.json (Mojang + Fabric/Quilt/Forge/NeoForge) ----------

public sealed class VersionInfo
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    /// <summary>Чей client.jar использовать (legacy Forge).</summary>
    public string? Jar { get; set; }
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    /// <summary>Старый формат аргументов (до 1.13), одна строка.</summary>
    public string? MinecraftArguments { get; set; }
    public Arguments? Arguments { get; set; }
    public List<Library> Libraries { get; set; } = new();
    public AssetIndexRef? AssetIndex { get; set; }
    public string? Assets { get; set; }
    public Dictionary<string, DownloadInfo>? Downloads { get; set; }
    public JavaVersionRef? JavaVersion { get; set; }
    public LoggingInfo? Logging { get; set; }
    /// <summary>Строкой: у Fabric/Forge формат даты нестандартный ("+0000" без двоеточия).</summary>
    public string? ReleaseTime { get; set; }
}

public sealed class Arguments
{
    /// <summary>Элементы — строка либо { rules, value: string | string[] }.</summary>
    public List<JsonElement> Game { get; set; } = new();
    public List<JsonElement> Jvm { get; set; } = new();
}

public sealed class Library
{
    /// <summary>Maven-координата group:artifact:version[:classifier][@ext]</summary>
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    /// <summary>Базовый URL maven-репозитория (Fabric/Forge), если нет downloads.</summary>
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<Rule>? Rules { get; set; }
    public ExtractRules? Extract { get; set; }
    /// <summary>Legacy Forge: false — библиотека не нужна клиенту.</summary>
    public bool? Clientreq { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadInfo? Artifact { get; set; }
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class DownloadInfo
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
    public string? Id { get; set; }
}

public sealed class ExtractRules
{
    public List<string> Exclude { get; set; } = new();
}

public sealed class AssetIndexRef
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

public sealed class JavaVersionRef
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingInfo
{
    public LoggingSide? Client { get; set; }
}

public sealed class LoggingSide
{
    public string Argument { get; set; } = "";
    public DownloadInfo File { get; set; } = new();
}

public sealed class AssetIndexFile
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
    public bool Virtual { get; set; }
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}

// ---------- Правила (rules) Mojang ----------

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Arch { get; set; }
    public string? Version { get; set; }
}

/// <summary>Флаги запуска, влияющие на правила аргументов.</summary>
public sealed record LaunchFeatures(bool IsDemoUser, bool HasCustomResolution, bool QuickPlayMultiplayer = false);

public static class Rules
{
    public static bool IsX86 => RuntimeInformation.OSArchitecture == Architecture.X86;
    public static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// Семантика Mojang: без правил — разрешено; иначе по умолчанию запрещено,
    /// и каждое подходящее правило перезаписывает решение своим action.
    /// </summary>
    public static bool Allows(List<Rule>? rules, LaunchFeatures? features = null)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var r in rules)
            if (Matches(r, features)) allowed = r.Action == "allow";
        return allowed;
    }

    private static bool Matches(Rule r, LaunchFeatures? f)
    {
        if (r.Os is { } os)
        {
            if (os.Name is not null && os.Name != "windows") return false;
            if (os.Arch is "x86" && !IsX86) return false;
            if (os.Version is not null &&
                !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), os.Version))
                return false;
        }

        if (r.Features is { } feats)
        {
            foreach (var (key, value) in feats)
            {
                var actual = key switch
                {
                    "is_demo_user" => f?.IsDemoUser ?? false,
                    "has_custom_resolution" => f?.HasCustomResolution ?? false,
                    "is_quick_play_multiplayer" => f?.QuickPlayMultiplayer ?? false,
                    _ => false, // quick play, has_quick_plays_support и т.п. не поддерживаем
                };
                if (actual != value) return false;
            }
        }

        return true;
    }

    public static string ArchBits => Environment.Is64BitOperatingSystem ? "64" : "32";
}

public static class Maven
{
    /// <summary>group:artifact:version[:classifier][@ext] → group/path/artifact/version/artifact-version[-classifier].ext</summary>
    public static string ToPath(string coordinate)
    {
        var ext = "jar";
        var at = coordinate.IndexOf('@');
        if (at >= 0)
        {
            ext = coordinate[(at + 1)..];
            coordinate = coordinate[..at];
        }

        var parts = coordinate.Split(':');
        if (parts.Length < 3) throw new FormatException(L.T($"Некорректная maven-координата: {coordinate}"));
        var (group, artifact, version) = (parts[0].Replace('.', '/'), parts[1], parts[2]);
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    /// <summary>Ключ для дедупликации библиотек при слиянии inheritsFrom: group:artifact[:classifier].</summary>
    public static string Key(string coordinate)
    {
        var parts = coordinate.Split('@')[0].Split(':');
        return parts.Length > 3 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : $"{parts[0]}:{parts[1]}";
    }
}
