namespace FrogLauncher.Models;

/// <summary>Мод в результатах поиска (Modrinth или CurseForge).</summary>
public sealed class ModProject
{
    public string Source { get; set; } = "";   // modrinth | curseforge
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string Author { get; set; } = "";
    public long Downloads { get; set; }

    public string DownloadsText => Downloads >= 1_000_000 ? $"{Downloads / 1_000_000.0:F1}M"
                                 : Downloads >= 1_000 ? $"{Downloads / 1_000.0:F0}K" : Downloads.ToString();
    public string SourceText => Source == "modrinth" ? "Modrinth" : "CurseForge";
}

public sealed record ModDep(string ProjectId, string? FileId);

/// <summary>Конкретный файл мода, который можно скачать.</summary>
public sealed class ModFileInfo
{
    public string Source { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
    public long Size { get; set; }
    public List<ModDep> Deps { get; set; } = new();
}

/// <summary>Запись о моде, установленном через лаунчер (instances/{id}/mods-meta.json). Нужна для экспорта сборок.</summary>
public sealed class InstalledModMeta
{
    public string Source { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
    public long Size { get; set; }
}
