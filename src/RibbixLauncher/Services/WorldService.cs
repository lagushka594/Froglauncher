using RibbixLauncher.Core;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed record WorldBackup(string Path, DateTime Created, long SizeBytes, bool Auto)
{
    public string Title => L.T($"{Created:dd.MM.yyyy HH:mm}{(Auto ? " · авто" : "")} · {Core.Fmt.Bytes(SizeBytes)}");
}

public sealed record WorldInfo(
    string Folder, string Name, DateTime? LastPlayed, string GameMode, string Difficulty, bool Hardcore,
    string? Version, long SizeBytes, string? IconPath, IReadOnlyList<WorldBackup> Backups)
{
    public string Summary => string.Join(" · ", new[]
    {
        Hardcore ? L.T("Хардкор") : GameMode, Difficulty, Version, Core.Fmt.Bytes(SizeBytes),
        LastPlayed is { } d ? L.T($"играли {d:dd.MM.yyyy HH:mm}") : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Миры версии: список (level.dat — NBT), бэкап в zip, восстановление, экспорт, удаление в корзину,
/// автобэкап раз в 24 часа (последние 5 автокопий на мир).
/// Бэкапы — instances/{id}/backups/{папка_мира}/{дата}.zip.
/// </summary>
public sealed class WorldService(ILogger<WorldService> log)
{
    public const int KeepAutoBackups = 5;
    public static readonly TimeSpan AutoInterval = TimeSpan.FromHours(24);

    private static string SavesDir(Instance inst) => Path.Combine(inst.GameDirectory, "saves");
    private static string BackupDir(Instance inst, string folder) => Path.Combine(inst.Directory, "backups", folder);

    public IReadOnlyList<WorldInfo> List(Instance inst)
    {
        var saves = SavesDir(inst);
        if (!Directory.Exists(saves)) return Array.Empty<WorldInfo>();
        var result = new List<WorldInfo>();
        foreach (var dir in Directory.GetDirectories(saves))
        {
            var folder = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "level.dat"))) continue;
            result.Add(Read(inst, dir, folder));
        }
        return result.OrderByDescending(w => w.LastPlayed ?? DateTime.MinValue).ToList();
    }

    private WorldInfo Read(Instance inst, string dir, string folder)
    {
        string name = folder, mode = "", diff = "";
        string? version = null;
        DateTime? last = null;
        var hardcore = false;
        try
        {
            var data = Nbt.ReadGzip(Path.Combine(dir, "level.dat"))?.GetValueOrDefault("Data") as Dictionary<string, object?>;
            if (data is not null)
            {
                name = data.GetValueOrDefault("LevelName") as string ?? folder;
                if (data.GetValueOrDefault("LastPlayed") is long ms) last = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                mode = (data.GetValueOrDefault("GameType") as int?) switch
                {
                    0 => L.T("Выживание"), 1 => L.T("Творчество"), 2 => L.T("Приключение"), 3 => L.T("Наблюдатель"), _ => "",
                };
                diff = Convert.ToInt32(data.GetValueOrDefault("Difficulty") ?? -1) switch
                {
                    0 => L.T("Мирная"), 1 => L.T("Лёгкая"), 2 => L.T("Нормальная"), 3 => L.T("Сложная"), _ => "",
                };
                hardcore = Convert.ToInt32(data.GetValueOrDefault("hardcore") ?? 0) == 1;
                version = (data.GetValueOrDefault("Version") as Dictionary<string, object?>)?.GetValueOrDefault("Name") as string;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or InvalidCastException)
        {
            log.LogWarning(ex, "Не прочитан level.dat мира {World}", folder);
        }

        var icon = Path.Combine(dir, "icon.png");
        return new WorldInfo(folder, name, last, mode, diff, hardcore, version, DirSize(dir), File.Exists(icon) ? icon : null, Backups(inst, folder));
    }

    public IReadOnlyList<WorldBackup> Backups(Instance inst, string folder)
    {
        var dir = BackupDir(inst, folder);
        if (!Directory.Exists(dir)) return Array.Empty<WorldBackup>();
        return new DirectoryInfo(dir).GetFiles("*.zip")
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new WorldBackup(f.FullName, f.CreationTime, f.Length, f.Name.Contains("-auto")))
            .ToList();
    }

    /// <summary>Бэкап мира в zip. session.lock пропускаем — его держит запущенная игра.</summary>
    public WorldBackup Backup(Instance inst, string folder, bool auto = false)
    {
        var src = Path.Combine(SavesDir(inst), folder);
        var dir = BackupDir(inst, folder);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{(auto ? "-auto" : "")}.zip");
        ZipDirectory(src, file);
        if (auto) PruneAuto(inst, folder);
        log.LogInformation("Бэкап мира {World} ({Auto}) → {File}", folder, auto ? L.T("авто") : L.T("вручную"), Path.GetFileName(file));
        var fi = new FileInfo(file);
        return new WorldBackup(file, fi.CreationTime, fi.Length, auto);
    }

    /// <summary>Восстановить из бэкапа. Текущее состояние мира сначала сохраняется отдельной копией.</summary>
    public void Restore(Instance inst, string folder, WorldBackup backup)
    {
        var dst = Path.Combine(SavesDir(inst), folder);
        if (Directory.Exists(dst))
        {
            var safety = Path.Combine(BackupDir(inst, folder), $"{DateTime.Now:yyyyMMdd-HHmmss}-before-restore.zip");
            ZipDirectory(dst, safety);
            Directory.Delete(dst, recursive: true);
        }
        Directory.CreateDirectory(dst);
        ExtractSafe(backup.Path, dst);
        log.LogInformation("Мир {World} восстановлен из {Backup}", folder, Path.GetFileName(backup.Path));
    }

    public void Export(Instance inst, string folder, string zipPath) => ZipDirectory(Path.Combine(SavesDir(inst), folder), zipPath);

    /// <summary>
    /// Импорт мира из .zip: корень мира — папка с самым «верхним» level.dat (архив может быть как с папкой мира внутри,
    /// так и без неё). Имя папки не перезаписывает существующий мир — добавляется « (2)». Возвращает имя папки.
    /// </summary>
    public string Import(Instance inst, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var level = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n == "level.dat" || n.EndsWith("/level.dat"))
            .OrderBy(n => n.Count(c => c == '/'))
            .FirstOrDefault() ?? throw new InvalidDataException(L.T("В архиве нет мира Minecraft (level.dat)"));
        var prefix = level[..^"level.dat".Length];
        var baseName = prefix.Length > 0 ? prefix.TrimEnd('/').Split('/')[^1] : Path.GetFileNameWithoutExtension(zipPath);
        baseName = string.Concat(baseName.Split(Path.GetInvalidFileNameChars())).Trim();
        if (baseName.Length == 0) baseName = "world";

        Directory.CreateDirectory(SavesDir(inst));
        var folder = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(SavesDir(inst), folder)); i++) folder = $"{baseName} ({i})";
        var dst = Path.Combine(SavesDir(inst), folder);
        try
        {
            PackTransferService.ExtractPrefix(zip, prefix, dst, maxBytes: 16L << 30);
        }
        catch
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
            throw;
        }
        log.LogInformation("Мир импортирован в {Id}: {Folder}", inst.Id, folder);
        return folder;
    }

    /// <summary>Удаление — в корзину Windows (можно вернуть).</summary>
    public void Delete(Instance inst, string folder)
    {
        var dir = Path.Combine(SavesDir(inst), folder);
        if (!Directory.Exists(dir)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        log.LogInformation("Мир {World} перемещён в корзину", folder);
    }

    /// <summary>Автобэкап: миры, в которые играли после последней копии, если копии старше 24 часов.</summary>
    public int AutoBackupAll(IEnumerable<Instance> instances)
    {
        var made = 0;
        foreach (var inst in instances)
            foreach (var w in List(inst))
            {
                var lastBackup = w.Backups.FirstOrDefault()?.Created;
                var dirty = lastBackup is null || (w.LastPlayed is { } lp && lp > lastBackup);
                if (!dirty || (lastBackup is { } lb && DateTime.Now - lb < AutoInterval)) continue;
                try { Backup(inst, w.Folder, auto: true); made++; }
                catch (IOException ex) { log.LogWarning(ex, "Автобэкап мира {World} не удался", w.Folder); }
            }
        return made;
    }

    private void PruneAuto(Instance inst, string folder)
    {
        foreach (var old in Backups(inst, folder).Where(b => b.Auto).Skip(KeepAutoBackups))
            File.Delete(old.Path);
    }

    private static void ZipDirectory(string src, string zipPath)
    {
        var tmp = zipPath + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "session.lock") continue;
                var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var es = zip.CreateEntry(rel, CompressionLevel.Optimal).Open();
                fs.CopyTo(es);
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    private static void ExtractSafe(string zipPath, string dst)
    {
        var root = Path.GetFullPath(dst).TrimEnd('\\') + "\\";
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(dst, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    private static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>Минимальный читатель NBT (формат level.dat Minecraft): gzip, big-endian, теги 1–12.</summary>
public static class Nbt
{
    public static Dictionary<string, object?>? ReadGzip(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        return Read(ms);
    }

    public static Dictionary<string, object?>? Read(Stream s)
    {
        var r = new BinaryReader(s);
        var type = r.ReadByte();
        if (type != 10) throw new InvalidDataException(L.T("Корень NBT — не compound"));
        ReadString(r); // имя корня
        return ReadCompound(r);
    }

    private static Dictionary<string, object?> ReadCompound(BinaryReader r)
    {
        var map = new Dictionary<string, object?>();
        while (true)
        {
            var type = r.ReadByte();
            if (type == 0) return map;
            var name = ReadString(r);
            map[name] = ReadPayload(r, type);
        }
    }

    private static object? ReadPayload(BinaryReader r, byte type) => type switch
    {
        1 => (byte)r.ReadSByte(),
        2 => BinaryPrimitives.ReadInt16BigEndian(r.ReadBytes(2)),
        3 => BinaryPrimitives.ReadInt32BigEndian(r.ReadBytes(4)),
        4 => BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8)),
        5 => BinaryPrimitives.ReadSingleBigEndian(r.ReadBytes(4)),
        6 => BinaryPrimitives.ReadDoubleBigEndian(r.ReadBytes(8)),
        7 => r.ReadBytes(Len(r)),
        8 => ReadString(r),
        9 => ReadList(r),
        10 => ReadCompound(r),
        11 => Enumerable.Range(0, Len(r)).Select(_ => BinaryPrimitives.ReadInt32BigEndian(r.ReadBytes(4))).ToArray(),
        12 => Enumerable.Range(0, Len(r)).Select(_ => BinaryPrimitives.ReadInt64BigEndian(r.ReadBytes(8))).ToArray(),
        _ => throw new InvalidDataException(L.T($"Неизвестный тег NBT {type}")),
    };

    private static List<object?> ReadList(BinaryReader r)
    {
        var itemType = r.ReadByte();
        var count = Len(r);
        var list = new List<object?>(Math.Min(count, 1024));
        for (var i = 0; i < count; i++) list.Add(ReadPayload(r, itemType));
        return list;
    }

    private static int Len(BinaryReader r)
    {
        var n = BinaryPrimitives.ReadInt32BigEndian(r.ReadBytes(4));
        if (n is < 0 or > 64 * 1024 * 1024) throw new InvalidDataException(L.T("Некорректная длина в NBT"));
        return n;
    }

    private static string ReadString(BinaryReader r)
    {
        var len = BinaryPrimitives.ReadUInt16BigEndian(r.ReadBytes(2));
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }
}
