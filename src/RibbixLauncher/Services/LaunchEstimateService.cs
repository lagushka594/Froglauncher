using System.IO;
using RibbixLauncher.Core;
using RibbixLauncher.Models;

namespace RibbixLauncher.Services;

/// <summary>Оценка первого запуска: сколько скачать, сколько займёт на диске и свободное место на нём.</summary>
public sealed record LaunchEstimate(long DownloadBytes, long DiskBytes, string Drive, long FreeBytes, double BytesPerSecond)
{
    /// <summary>Сверху к нужному месту: миры, логи, кэш.</summary>
    public const long DiskReserve = 1L << 30;
    /// <summary>Скорость, пока лаунчер ещё ничего не качал, — 3 МБ/с.</summary>
    public const double DefaultSpeed = 3 << 20;

    /// <summary>Мелкие докачки не стоят сообщения.</summary>
    public bool Worth => DownloadBytes >= 20L << 20;
    public TimeSpan Time => TimeSpan.FromSeconds(Math.Max(5, DownloadBytes / (BytesPerSecond > 64 * 1024 ? BytesPerSecond : DefaultSpeed)));
    public long NeedBytes => DiskBytes + DiskReserve;
    public bool LowDisk => FreeBytes >= 0 && FreeBytes < NeedBytes;
}

/// <summary>«⏱ Сколько качать»: перед первым запуском версии считаем недостающие файлы и Java, время — по прошлой скорости.</summary>
public sealed class LaunchEstimateService(VersionService versions, JavaService java, SettingsService settings)
{
    /// <summary>JRE Temurin: архив и распакованная.</summary>
    public const long JavaDownload = 48L << 20, JavaUnpacked = 150L << 20;

    public async Task<LaunchEstimate> EstimateAsync(Instance inst, CancellationToken ct)
    {
        var download = await versions.MissingBytesAsync(inst.GameVersion, ct);
        var disk = download;
        var hasJava = !string.IsNullOrEmpty(inst.JavaPath) && File.Exists(inst.JavaPath);
        if (!hasJava)
        {
            var major = JavaService.RequiredMajor(await versions.EnsureVanillaJsonAsync(inst.GameVersion, ct));
            if (!(await java.DetectAsync(ct: ct)).Any(j => j.Major == major))
            {
                download += JavaDownload;
                disk += JavaUnpacked;
            }
        }
        var (drive, free) = FreeSpace(LauncherPaths.Root);
        return new LaunchEstimate(download, disk, drive, free, settings.Current.DownloadSpeedBps);
    }

    /// <summary>Запомнить скорость последней большой загрузки — по ней следующая оценка времени.</summary>
    public void RememberSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 64 * 1024) return;
        settings.Current.DownloadSpeedBps = bytesPerSecond;
        settings.Save();
    }

    private static (string Drive, long Free) FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return (root.TrimEnd('\\'), new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return ("", -1); }
    }

    public static string Describe(LaunchEstimate e) =>
        L.T($"⏱ Первая загрузка версии: {Fmt.Bytes(e.DownloadBytes)}, примерно {Fmt.Eta(e.Time)}. Потом запуск будет быстрым.");

    public static string LowDiskText(LaunchEstimate e) =>
        L.T($"На диске {e.Drive} свободно {Fmt.Bytes(e.FreeBytes)}, а версии нужно около {Fmt.Bytes(e.NeedBytes)}. Загрузка может оборваться на середине.\n\nОсвободите место или всё равно начать?");
}
