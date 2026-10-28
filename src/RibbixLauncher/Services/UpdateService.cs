using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace RibbixLauncher.Services;

public sealed record UpdateInfo(string Version, bool IsPrerelease, string SetupUrl, string? Sha256, string PageUrl, string Notes);

/// <summary>
/// Автообновление из GitHub Releases: stable — последний релиз, beta — включая pre-release (для Ribbix+ и выше).
/// Установщик скачивается в фоне, проверяется по SHA-256 (digest ассета на GitHub или строка «sha256» в описании релиза)
/// и запускается в тихом режиме с перезапуском лаунчера.
/// </summary>
public sealed partial class UpdateService(
    IHttpClientFactory httpFactory,
    IOptions<LauncherOptions> options,
    SettingsService settings,
    EntitlementService entitlements,
    TierCatalog tiers,
    ILogger<UpdateService> log)
{
    public const string SetupAsset = "RibbixLauncherSetup.exe";
    /// <summary>Имя установщика до переименования — в старых релизах.</summary>
    public const string LegacySetupAsset = "FrogLauncherSetup.exe";

    public static string CurrentVersion => LaunchService.LauncherVersion;

    /// <summary>Готовое к установке обновление (уже скачано и проверено).</summary>
    public UpdateInfo? Ready { get; private set; }
    public string? ReadyFile { get; private set; }
    public event Action? ReadyChanged;
    /// <summary>Скачивание установщика: версия, скачано и всего байт (из фонового потока).</summary>
    public event Action<string, long, long>? DownloadProgress;

    private readonly object _gate = new();
    private Task<UpdateInfo?>? _running;

    /// <summary>beta-канал — Ribbix+ и выше (включая Admin).</summary>
    public bool CanUseBeta => tiers.RankOf(entitlements.Tier) >= tiers.RankOf(TierSlugs.RibbixPlus);

    public string Channel => settings.Current.UpdateChannel == "beta" && CanUseBeta ? "beta" : "stable";

    /// <summary>Обновляться можно только установленной копии (а не из папки разработки).</summary>
    public static bool IsInstalledCopy
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\RibbixLauncher");
            var dir = key?.GetValue("InstallLocation") as string;
            return dir is not null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'),
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient(DownloadService.HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{options.Value.UpdateRepository}/releases?per_page=15");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogInformation("Проверка обновлений: GitHub ответил {Status}", (int)resp.StatusCode);
            return null;
        }

        return PickUpdate(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsArray(), CurrentVersion, Channel == "beta");
    }

    /// <summary>
    /// Самая новая подходящая версия. GitHub сортирует релизы по дате создания, а не по номеру
    /// (v1.0.1 может оказаться выше V2.0.0) — поэтому сравниваем все.
    /// </summary>
    internal static UpdateInfo? PickUpdate(JsonArray releases, string current, bool beta)
    {
        UpdateInfo? best = null;
        foreach (var r in releases)
        {
            if (r!["draft"]?.GetValue<bool>() == true) continue;
            var pre = r["prerelease"]?.GetValue<bool>() == true;
            if (pre && !beta) continue;

            var version = (r["tag_name"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (!IsNewer(version, current) || (best is not null && !IsNewer(version, best.Version))) continue;

            var asset = r["assets"]?.AsArray().FirstOrDefault(a => a!["name"]?.GetValue<string>() == SetupAsset)
                        ?? r["assets"]?.AsArray().FirstOrDefault(a => a!["name"]?.GetValue<string>() == LegacySetupAsset);
            if (asset is null) continue;
            var body = r["body"]?.GetValue<string>() ?? "";
            var sha = asset["digest"]?.GetValue<string>() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)
                ? d["sha256:".Length..]
                : Sha256Regex().Match(body) is { Success: true } m ? m.Groups[1].Value : null;

            best = new UpdateInfo(version, pre, asset["browser_download_url"]!.GetValue<string>(), sha?.ToLowerInvariant(),
                r["html_url"]?.GetValue<string>() ?? "", body);
        }
        return best;
    }

    /// <summary>
    /// Проверить и тихо скачать обновление в фоне. Возвращает найденное обновление (или null).
    /// Повторный вызов во время загрузки (фон при запуске + «Проверить сейчас») ждёт ту же загрузку.
    /// </summary>
    public Task<UpdateInfo?> CheckAndDownloadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return _running;
            return _running = RunAsync(ct);
        }
    }

    private async Task<UpdateInfo?> RunAsync(CancellationToken ct)
    {
        var info = await CheckAsync(ct);
        if (info is null || info.Version == settings.Current.SkippedUpdate) return info;
        if (info.Sha256 is null)
        {
            log.LogWarning("Релиз {Version} без SHA-256 — автоматически не ставим", info.Version);
            return info;
        }

        var file = Path.Combine(LauncherPaths.Cache, "updates", $"RibbixLauncherSetup-{info.Version}.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file) || await Hashing.Sha256FileAsync(file, ct) != info.Sha256)
        {
            var http = httpFactory.CreateClient(DownloadService.HttpClientName);
            var tmp = file + ".part";
            // Докачка: на медленном интернете лаунчер могут закрыть посреди загрузки — продолжаем с того же места.
            var have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            using var get = new HttpRequestMessage(HttpMethod.Get, info.SetupUrl);
            if (have > 0) get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using (var resp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) have = 0; // .part уже целиком — проверим хэш ниже
                else resp.EnsureSuccessStatusCode();
                var resume = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resume && resp.IsSuccessStatusCode) have = 0;
                var total = have + (resp.Content.Headers.ContentLength ?? 0);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = resp.IsSuccessStatusCode ? new FileStream(tmp, resume ? FileMode.Append : FileMode.Create) : Stream.Null;
                var buf = new byte[1 << 16];
                long done = have, reported = have;
                int n;
                DownloadProgress?.Invoke(info.Version, have, total);
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (done - reported >= 512 * 1024)
                    {
                        reported = done;
                        DownloadProgress?.Invoke(info.Version, done, total);
                    }
                }
            }
            if (await Hashing.Sha256FileAsync(tmp, ct) != info.Sha256)
            {
                File.Delete(tmp);
                log.LogWarning("Обновление {Version}: SHA-256 не совпал, файл удалён", info.Version);
                return info;
            }
            File.Move(tmp, file, overwrite: true);
        }

        Ready = info;
        ReadyFile = file;
        ReadyChanged?.Invoke();
        log.LogInformation("Обновление {Version} скачано и проверено", info.Version);
        return info;
    }

    /// <summary>Запустить установщик тихо (он дождётся закрытия лаунчера и перезапустит его) и выйти.</summary>
    public bool ApplyAndRestart()
    {
        if (ReadyFile is null || !File.Exists(ReadyFile)) return false;
        Process.Start(new ProcessStartInfo(ReadyFile)
        {
            UseShellExecute = false,
            Arguments = $"--silent --relaunch --dir \"{AppContext.BaseDirectory.TrimEnd('\\')}\"",
        });
        // Закрываемся по-настоящему, а не в трей.
        (App.Services.GetService(typeof(TrayService)) as TrayService)?.MarkExiting();
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    public void Skip()
    {
        if (Ready is null) return;
        settings.Current.SkippedUpdate = Ready.Version;
        settings.Save();
        Ready = null;
        ReadyChanged?.Invoke();
    }

    /// <summary>2.2.0 &gt; 2.1.0; 2.2.0 &gt; 2.2.0-beta.1; 2.2.0-beta.2 &gt; 2.2.0-beta.1.</summary>
    internal static bool IsNewer(string candidate, string current)
    {
        static (Version v, string pre) Split(string s)
        {
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            var pre = dash >= 0 ? s[(dash + 1)..] : "";
            var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
            return (new Version(parts[0], parts[1], parts[2]), pre);
        }

        var (cv, cp) = Split(candidate);
        var (kv, kp) = Split(current);
        var cmp = cv.CompareTo(kv);
        if (cmp != 0) return cmp > 0;
        if (cp.Length == 0) return kp.Length > 0;        // релиз новее своей беты
        if (kp.Length == 0) return false;                // бета не новее релиза
        return string.CompareOrdinal(cp, kp) > 0;
    }

    [GeneratedRegex(@"sha256[^0-9a-fA-F]{0,20}([0-9a-fA-F]{64})", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Regex();
}
