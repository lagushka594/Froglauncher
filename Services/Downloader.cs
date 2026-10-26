using System.Diagnostics;
using System.Security.Cryptography;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

public sealed record DownloadItem(string Url, string Path, string? Sha1 = null, long Size = 0);

/// <summary>
/// Многопоточная загрузка с SHA1-проверкой, прогрессом (%, скорость, ETA)
/// и повторами (3 попытки, экспоненциальная задержка).
/// </summary>
public static class Downloader
{
    /// <summary>Потоков загрузки: 8, а с привилегией Frog+ (опция «Быстрая загрузка») — 16.</summary>
    public static int Parallelism { get; set; } = 8;
    public const int Retries = 3;

    public static async Task<bool> IsValidAsync(string path, string? sha1, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrEmpty(sha1)) return new FileInfo(path).Length > 0;
        await using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA1.HashDataAsync(fs, ct));
        return hash.Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DownloadAsync(IReadOnlyList<DownloadItem> items, IProgress<DownloadProgress>? progress,
        CancellationToken ct, string stage = "")
    {
        long total = items.Sum(i => i.Size);
        long done = 0;
        int filesDone = 0;
        var sw = Stopwatch.StartNew();
        long lastReport = 0;

        void Report(bool force = false)
        {
            if (progress == null) return;
            var ms = sw.ElapsedMilliseconds;
            if (!force && ms - lastReport < 120) return;
            lastReport = ms;
            var d = Interlocked.Read(ref done);
            var t = Interlocked.Read(ref total);
            var speed = ms > 0 ? d * 1000.0 / ms : 0;
            TimeSpan? eta = speed > 1 && t > d ? TimeSpan.FromSeconds((t - d) / speed) : null;
            progress.Report(new DownloadProgress
            {
                BytesDone = d, BytesTotal = t, FilesDone = filesDone, FilesTotal = items.Count,
                SpeedBytes = speed, Eta = eta, Stage = stage
            });
        }

        var opts = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(items, opts, async (item, token) =>
        {
            // Forge/NeoForge: часть библиотек собирает сам инсталлятор — url пустой, файл уже на диске
            if (string.IsNullOrEmpty(item.Url) && File.Exists(item.Path))
            {
                Interlocked.Add(ref done, item.Size);
                Interlocked.Increment(ref filesDone);
                Report();
                return;
            }
            if (string.IsNullOrEmpty(item.Url))
                throw new FileNotFoundException("Файл должен был создать инсталлятор модлоадера: " + item.Path);

            if (await IsValidAsync(item.Path, item.Sha1, token))
            {
                Interlocked.Add(ref done, item.Size);
                Interlocked.Increment(ref filesDone);
                Report();
                return;
            }

            Exception? last = null;
            for (int attempt = 1; attempt <= Retries; attempt++)
            {
                long counted = 0;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
                    var tmp = item.Path + ".part";
                    using var resp = await Http.Client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, token);
                    resp.EnsureSuccessStatusCode();
                    if (item.Size == 0 && resp.Content.Headers.ContentLength is long len)
                    {
                        Interlocked.Add(ref total, len); // размер заранее неизвестен
                        counted = len;
                    }
                    long thisFile = 0;
                    await using (var src = await resp.Content.ReadAsStreamAsync(token))
                    await using (var dst = File.Create(tmp))
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buf, token)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), token);
                            thisFile += n;
                            Interlocked.Add(ref done, n);
                            Report();
                        }
                    }
                    if (!string.IsNullOrEmpty(item.Sha1) && !await IsValidAsync(tmp, item.Sha1, token))
                    {
                        Interlocked.Add(ref done, -thisFile);
                        File.Delete(tmp);
                        throw new InvalidDataException($"SHA1 не совпал: {item.Url}");
                    }
                    File.Move(tmp, item.Path, true);
                    Interlocked.Increment(ref filesDone);
                    Report();
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn($"Загрузка {item.Url}, попытка {attempt}/{Retries}: {ex.Message}");
                    if (counted > 0) { /* общий размер оставляем — докачаем на следующей попытке */ }
                    if (attempt < Retries) await Task.Delay(500 * (1 << attempt), token);
                }
            }
            throw new IOException($"Не удалось скачать {item.Url}: {last?.Message}", last);
        });
        Report(true);
    }

    public static string FormatSpeed(double bps) =>
        bps >= 1024 * 1024 ? $"{bps / 1024 / 1024:F1} МБ/с" : $"{bps / 1024:F0} КБ/с";

    public static string FormatProgress(DownloadProgress p)
    {
        var s = $"{p.Percent:F0}%  •  {FormatSpeed(p.SpeedBytes)}";
        if (p.Eta is TimeSpan e) s += $"  •  ~{(int)e.TotalMinutes}:{e.Seconds:D2}";
        return s;
    }
}
