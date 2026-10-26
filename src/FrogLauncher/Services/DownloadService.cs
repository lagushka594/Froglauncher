using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FrogLauncher.Core;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace FrogLauncher.Services;

public sealed record DownloadItem(string Url, string Path, string? Sha1, long Size, bool QuickCheck = false);

/// <summary>Прогресс загрузки для UI: файлы, байты, скорость, ETA.</summary>
public sealed record DownloadProgress(
    string Stage, int DoneFiles, int TotalFiles, long DoneBytes, long TotalBytes, double BytesPerSecond)
{
    public double Percent => TotalBytes > 0
        ? Math.Min(100, DoneBytes * 100.0 / TotalBytes)
        : TotalFiles > 0 ? DoneFiles * 100.0 / TotalFiles : 0;

    public TimeSpan? Eta => BytesPerSecond > 1 && TotalBytes > DoneBytes
        ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond)
        : null;

    public static DownloadProgress Indeterminate(string stage) => new(stage, 0, 0, 0, 0, 0);
}

public sealed class HashMismatchException(string path) : IOException($"Контрольная сумма не совпала: {System.IO.Path.GetFileName(path)}");

/// <summary>
/// Многопоточная загрузка с проверкой SHA1.
/// Parallel.ForEachAsync распараллеливает файлы, глобальный SemaphoreSlim ограничивает число
/// одновременных соединений на весь лаунчер (даже если идут две установки сразу).
/// Каждый файл — Polly: 3 повтора с экспоненциальной задержкой.
/// </summary>
public sealed class DownloadService(IHttpClientFactory httpFactory, ILogger<DownloadService> log)
{
    public const string HttpClientName = "downloads";
    private static readonly SemaphoreSlim Connections = new(16);

    private readonly AsyncRetryPolicy _retry = Policy
        .Handle<HttpRequestException>()
        .Or<IOException>()
        .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

    /// <summary>Параллелизм: 8 потоков, с фичей priority_download — 16.</summary>
    public int Parallelism { get; set; } = 8;

    public async Task DownloadAsync(IReadOnlyCollection<DownloadItem> items, string stage,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Пропускаем уже скачанные файлы (проверка SHA1 или размера).
        var pending = new List<DownloadItem>();
        foreach (var item in items.DistinctBy(i => i.Path.ToLowerInvariant()))
            if (!await IsValidAsync(item, ct)) pending.Add(item);

        if (pending.Count == 0) return;

        long total = pending.Sum(i => i.Size), done = 0;
        int files = 0;
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var gate = new object();

        void Report(bool force = false)
        {
            lock (gate)
            {
                if (!force && sw.Elapsed - lastReport < TimeSpan.FromMilliseconds(150)) return;
                lastReport = sw.Elapsed;
                var speed = Interlocked.Read(ref done) / Math.Max(0.5, sw.Elapsed.TotalSeconds);
                progress?.Report(new DownloadProgress(stage, files, pending.Count, Interlocked.Read(ref done), total, speed));
            }
        }

        log.LogInformation("{Stage}: {Count} файлов, {Size}", stage, pending.Count, Fmt.Bytes(total));
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (item, token) =>
            {
                await Connections.WaitAsync(token);
                try
                {
                    await _retry.ExecuteAsync(innerCt => DownloadOneAsync(item, b =>
                    {
                        Interlocked.Add(ref done, b);
                        Report();
                    }, innerCt), token);
                }
                finally
                {
                    Connections.Release();
                }

                Interlocked.Increment(ref files);
                Report();
            });
        Report(force: true);
    }

    public Task DownloadFileAsync(DownloadItem item, CancellationToken ct) =>
        DownloadAsync(new[] { item }, Path.GetFileName(item.Path), null, ct);

    private async Task DownloadOneAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
        var tmp = item.Path + ".part";
        long written = 0;
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    onBytes(read);
                }
            }

            if (item.Sha1 is { Length: > 0 } sha && !string.Equals(await Hashing.Sha1FileAsync(tmp, ct), sha, StringComparison.OrdinalIgnoreCase))
                throw new HashMismatchException(item.Path);

            File.Move(tmp, item.Path, overwrite: true);
        }
        catch
        {
            onBytes(-written); // при повторе прогресс не должен «перескакивать» 100%
            TryDelete(tmp);
            throw;
        }
    }

    private static async Task<bool> IsValidAsync(DownloadItem item, CancellationToken ct)
    {
        var fi = new FileInfo(item.Path);
        if (!fi.Exists) return false;
        if (item.Size > 0 && fi.Length != item.Size) return false;
        // Ассеты названы по своему хэшу — достаточно размера, иначе первый запуск проверяет тысячи файлов.
        if (item.QuickCheck || string.IsNullOrEmpty(item.Sha1)) return item.Size > 0 || fi.Length > 0;
        return string.Equals(await Hashing.Sha1FileAsync(item.Path, ct), item.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
