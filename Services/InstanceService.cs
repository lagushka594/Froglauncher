using System.IO.Compression;
using System.Text.Json;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Инстансы: instances/{id}/instance.json + изолированная папка .minecraft.</summary>
public sealed class InstanceService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<Instance> LoadAll()
    {
        var list = new List<Instance>();
        if (!Directory.Exists(AppPaths.Instances)) return list;
        foreach (var dir in Directory.GetDirectories(AppPaths.Instances))
        {
            var f = Path.Combine(dir, "instance.json");
            try
            {
                if (!File.Exists(f)) continue;
                var inst = JsonSerializer.Deserialize<Instance>(File.ReadAllText(f));
                if (inst != null) { inst.Id = Path.GetFileName(dir); list.Add(inst); }
            }
            catch (Exception ex) { Log.Warn($"Инстанс {dir} повреждён: {ex.Message}"); }
        }
        return list.OrderBy(i => i.Created).ToList();
    }

    public void Save(Instance inst)
    {
        Directory.CreateDirectory(inst.GameDir);
        Directory.CreateDirectory(Path.Combine(inst.GameDir, "mods"));
        File.WriteAllText(Path.Combine(inst.Dir, "instance.json"), JsonSerializer.Serialize(inst, Json));
    }

    public void Delete(Instance inst)
    {
        if (Directory.Exists(inst.Dir)) Directory.Delete(inst.Dir, true);
    }

    public void Export(Instance inst, string zipPath)
    {
        Save(inst);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(inst.Dir, zipPath, CompressionLevel.Optimal, false);
    }

    /// <summary>Копия версии вместе с модами, настройками и мирами.</summary>
    public Instance Clone(Instance src)
    {
        Save(src);
        var copy = JsonSerializer.Deserialize<Instance>(JsonSerializer.Serialize(src))!;
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name = src.Name + " (копия)";
        copy.PlaySeconds = 0; copy.LastPlayed = null; copy.Created = DateTime.Now;
        CopyDir(src.Dir, copy.Dir, skip: new[] { "natives", "backups" });
        Save(copy);
        return copy;
    }

    private static void CopyDir(string from, string to, string[] skip)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
        {
            if (skip.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)), skip);
        }
    }

    /// <summary>Резервная копия миров в instances/{id}/backups. Возвращает путь или null, если миров нет.</summary>
    public string? BackupWorlds(Instance inst)
    {
        var saves = Path.Combine(inst.GameDir, "saves");
        if (!Directory.Exists(saves) || !Directory.EnumerateFileSystemEntries(saves).Any()) return null;
        var dir = Path.Combine(inst.Dir, "backups");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, $"saves-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(saves, zip, CompressionLevel.Optimal, false);
        return zip;
    }

    /// <summary>Оставляем только последние N резервных копий миров.</summary>
    public void PruneBackups(Instance inst, int keep)
    {
        var dir = Path.Combine(inst.Dir, "backups");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "saves-*.zip").OrderByDescending(f => f).Skip(keep))
            try { File.Delete(f); } catch { }
    }

    public Instance Import(string zipPath)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dest = Path.Combine(AppPaths.Instances, id);
        Directory.CreateDirectory(dest);
        var full = Path.GetFullPath(dest);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
        }
        var f = Path.Combine(dest, "instance.json");
        if (!File.Exists(f)) { Directory.Delete(dest, true); throw new InvalidDataException("В архиве нет instance.json."); }
        var inst = JsonSerializer.Deserialize<Instance>(File.ReadAllText(f)) ?? throw new InvalidDataException("instance.json повреждён.");
        inst.Id = id;
        inst.Name += " (импорт)";
        Save(inst);
        return inst;
    }
}

/// <summary>Метаданные модов, установленных через лаунчер.</summary>
public static class ModMetaStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string PathFor(Instance i) => System.IO.Path.Combine(i.Dir, "mods-meta.json");

    public static List<InstalledModMeta> Load(Instance inst)
    {
        try
        {
            var p = PathFor(inst);
            if (File.Exists(p)) return JsonSerializer.Deserialize<List<InstalledModMeta>>(File.ReadAllText(p)) ?? new();
        }
        catch (Exception ex) { Log.Warn("mods-meta.json: " + ex.Message); }
        return new();
    }

    public static void Save(Instance inst, List<InstalledModMeta> list)
    {
        Directory.CreateDirectory(inst.Dir);
        File.WriteAllText(PathFor(inst), JsonSerializer.Serialize(list, Json));
    }

    public static void Remove(Instance inst, string fileName)
    {
        var list = Load(inst);
        if (list.RemoveAll(m => m.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)) > 0) Save(inst, list);
    }
}
