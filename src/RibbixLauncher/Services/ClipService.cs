using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed record Capture(string Path, DateTime Taken, long Size);

/// <summary>
/// Клип для TikTok: последние 30 секунд из записи Xbox Game Bar (Win+Alt+G) → вертикальное видео 1080×1920
/// с размытым фоном и логотипом. ffmpeg лаунчер скачивает один раз (сборка BtbN с GitHub) в свою папку.
/// </summary>
public sealed partial class ClipService(IHttpClientFactory httpFactory, ILogger<ClipService> log)
{
    public const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public const int Seconds = 30;

    public static string FfmpegPath => Path.Combine(LauncherPaths.Root, "tools", "ffmpeg.exe");
    public static bool HasFfmpeg => File.Exists(FfmpegPath);

    public static string CapturesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
    public static string OutputDir => Path.Combine(ShareCardService.OutputDir, L.T("Клипы"));

    /// <summary>Записи Game Bar, новые сверху.</summary>
    public static IReadOnlyList<Capture> List()
    {
        if (!Directory.Exists(CapturesDir)) return [];
        return new DirectoryInfo(CapturesDir).GetFiles("*.mp4")
            .Select(f => new Capture(f.FullName, f.LastWriteTime, f.Length))
            .OrderByDescending(c => c.Taken).ToList();
    }

    public async Task EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (HasFfmpeg) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FfmpegPath)!);
        var zip = FfmpegPath + ".zip.part";
        using (var resp = await httpFactory.CreateClient(DownloadService.HttpClientName).GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(L.T("В архиве нет ffmpeg.exe"));
            entry.ExtractToFile(FfmpegPath + ".part", true);
            File.Move(FfmpegPath + ".part", FfmpegPath, true);
            log.LogInformation("ffmpeg скачан: {Path}", FfmpegPath);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    /// <summary>Аргументы ffmpeg: последние <paramref name="seconds"/> с, 9:16 с размытым фоном, логотип сверху.</summary>
    internal static List<string> BuildArgs(string input, string output, string? logo, int seconds = Seconds)
    {
        var filter = "[0:v]split=2[a][b];" +
                     "[a]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,boxblur=24:2,eq=brightness=-0.08[bg];" +
                     "[b]scale=1080:-2[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2" + (logo is null ? "[v]" : "[v0];[1:v]scale=520:-1[lg];[v0][lg]overlay=(W-w)/2:150[v]");
        var args = new List<string> { "-hide_banner", "-y", "-sseof", "-" + seconds.ToString(CultureInfo.InvariantCulture), "-i", input };
        if (logo is not null) args.AddRange(["-i", logo]);
        args.AddRange(["-filter_complex", filter, "-map", "[v]", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output]);
        return args;
    }

    [GeneratedRegex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();

    /// <summary>Сделать клип. Возвращает путь к готовому mp4.</summary>
    public async Task<string> MakeAsync(Capture capture, IProgress<double>? progress, CancellationToken ct)
    {
        if (!HasFfmpeg) throw new InvalidOperationException(L.T("ffmpeg ещё не скачан"));
        Directory.CreateDirectory(OutputDir);
        var output = Path.Combine(OutputDir, $"ribbix-clip-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var logo = ExtractLogo();
        var psi = new ProcessStartInfo(FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in BuildArgs(capture.Path, output, logo)) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(L.T("ffmpeg не запустился"));
        var tail = new Queue<string>();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 8) tail.Dequeue();
            }
            if (TimeRegex().Match(line) is { Success: true } m)
            {
                var t = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                progress?.Report(Math.Min(100, t * 100 / Seconds));
            }
        };
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        if (proc.ExitCode != 0 || !File.Exists(output))
        {
            string msg;
            lock (tail) msg = string.Join(" ", tail);
            log.LogWarning("ffmpeg {Code}: {Tail}", proc.ExitCode, msg);
            try { File.Delete(output); } catch (IOException) { }
            throw new InvalidOperationException(L.T($"ffmpeg завершился с кодом {proc.ExitCode}"));
        }
        return output;
    }

    private static string? ExtractLogo()
    {
        try
        {
            var path = Path.Combine(LauncherPaths.Cache, "clip-logo.png");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(LauncherPaths.Cache);
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/main/logo.png"));
            if (res is null) return null;
            using var s = res.Stream;
            using var f = File.Create(path);
            s.CopyTo(f);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
