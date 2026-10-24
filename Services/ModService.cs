using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Поиск и установка модов из Modrinth / CurseForge (с обязательными зависимостями).</summary>
public sealed class ModService
{
    public static readonly string[] Sources = { "Modrinth", "CurseForge" };

    private readonly ModrinthService _mr = new();
    private readonly CurseForgeService _cf;

    public ModService(Func<string> curseForgeKey) { _cf = new CurseForgeService(curseForgeKey); }
    public CurseForgeService CurseForge => _cf;

    private static string? MrLoader(Instance i) => i.Loader == "Vanilla" ? null : i.Loader.ToLowerInvariant();

    private static void RequireLoader(Instance i)
    {
        if (i.Loader == "Vanilla")
            throw new InvalidOperationException("У этой версии нет модлоадера. Создайте версию с Fabric, Forge, Quilt или NeoForge.");
    }

    public Task<List<ModProject>> SearchAsync(string source, string query, Instance inst, int offset, CancellationToken ct)
    {
        RequireLoader(inst);
        return source == "CurseForge"
            ? _cf.SearchAsync(query, inst.McVersion, inst.Loader, offset, ct)
            : _mr.SearchAsync(query, inst.McVersion, MrLoader(inst), offset, ct);
    }

    /// <summary>Проверяет все моды, установленные через лаунчер, и обновляет те, для которых вышла новая версия.</summary>
    public async Task<List<string>> UpdateAllAsync(Instance inst, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        RequireLoader(inst);
        var modsDir = Path.Combine(inst.GameDir, "mods");
        var updated = new List<string>();
        foreach (var m in ModMetaStore.Load(inst).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(modsDir, m.FileName)) || string.IsNullOrEmpty(m.ProjectId)) continue; // отключён/неизвестен
            ModFileInfo? latest;
            try
            {
                latest = m.Source == "curseforge"
                    ? await _cf.GetLatestFileAsync(m.ProjectId, m.Name, inst.McVersion, inst.Loader, ct)
                    : await _mr.GetLatestFileAsync(m.ProjectId, m.Name, inst.McVersion, MrLoader(inst), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Обновление {m.Name}: {ex.Message}"); continue; }
            if (latest == null || latest.FileId == m.FileId) continue;
            await InstallAsync(new ModProject { Source = m.Source, Id = m.ProjectId, Name = m.Name }, inst, progress, ct);
            updated.Add(m.Name);
        }
        return updated;
    }

    /// <summary>Скачивает мод и его обязательные зависимости в mods/ инстанса. Возвращает названия установленного.</summary>
    public async Task<List<string>> InstallAsync(ModProject project, Instance inst, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        RequireLoader(inst);
        var modsDir = Path.Combine(inst.GameDir, "mods");
        Directory.CreateDirectory(modsDir);

        var meta = ModMetaStore.Load(inst);
        var queue = new Queue<(string source, string projectId, string name, string? fileId)>();
        queue.Enqueue((project.Source, project.Id, project.Name, null));
        var seen = new HashSet<string>();
        var toInstall = new List<ModFileInfo>();

        while (queue.Count > 0 && seen.Count < 60)
        {
            var (source, pid, name, fileId) = queue.Dequeue();
            if (!seen.Add(source + ":" + pid)) continue;
            // зависимость уже стоит — пропускаем (сам выбранный мод всегда обновляем)
            if (pid != project.Id && meta.Any(m => m.Source == source && m.ProjectId == pid)) continue;

            ModFileInfo? file;
            if (source == "curseforge")
                file = await _cf.GetLatestFileAsync(pid, name, inst.McVersion, inst.Loader, ct);
            else if (fileId != null)
                file = await _mr.GetVersionAsync(fileId, name, ct);
            else
                file = await _mr.GetLatestFileAsync(pid, name, inst.McVersion, MrLoader(inst), ct);

            if (file == null)
            {
                if (pid == project.Id)
                    throw new InvalidOperationException($"У «{name}» нет версии под Minecraft {inst.McVersion} ({inst.Loader}).");
                Log.Warn($"Зависимость {pid} не найдена для {inst.McVersion}/{inst.Loader}");
                continue;
            }
            if (pid != project.Id)
                file.Name = source == "curseforge" ? await _cf.GetModNameAsync(pid, ct) : await _mr.GetProjectNameAsync(pid, ct);
            toInstall.Add(file);
            foreach (var d in file.Deps) queue.Enqueue((source, d.ProjectId, d.ProjectId, d.FileId));
        }

        // удаляем старые версии тех же проектов
        foreach (var f in toInstall)
        {
            foreach (var old in meta.Where(m => m.Source == f.Source && m.ProjectId == f.ProjectId && m.FileName != f.FileName).ToList())
            {
                foreach (var suffix in new[] { "", ".disabled" })
                {
                    var oldPath = Path.Combine(modsDir, old.FileName + suffix);
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                }
                meta.Remove(old);
            }
        }

        var items = toInstall.Select(f => new DownloadItem(f.Url, Path.Combine(modsDir, f.FileName), f.Sha1, f.Size)).ToList();
        await Downloader.DownloadAsync(items, progress, ct, "Моды");

        foreach (var f in toInstall)
        {
            meta.RemoveAll(m => m.FileName == f.FileName);
            meta.Add(new InstalledModMeta
            {
                Source = f.Source, ProjectId = f.ProjectId, FileId = f.FileId, Name = f.Name, FileName = f.FileName,
                Url = f.Url, Sha1 = f.Sha1, Sha512 = f.Sha512, Size = f.Size
            });
        }
        ModMetaStore.Save(inst, meta);
        return toInstall.Select(f => string.IsNullOrEmpty(f.Name) ? f.FileName : f.Name).ToList();
    }
}
