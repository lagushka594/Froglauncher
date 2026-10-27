using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Frog.Shared;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

/// <summary>Лимит инстансов тира — повод для мягкого апселла, а не ошибка.</summary>
public sealed class InstanceLimitException(string tier, int limit)
    : Exception(L.T($"У {TierNames.Display(tier)} максимум {limit} {VersionsWord(limit)}. Froglet — 50, Frog+ — ∞."))
{
    public string Tier { get; } = tier;
    public int Limit { get; } = limit;

    private static string VersionsWord(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => L.T("версия"),
        (>= 2 and <= 4, < 12 or > 14) => L.T("версии"),
        _ => L.T("версий"),
    };
}

public static class TierNames
{
    public static string Display(string slug) => slug switch
    {
        TierSlugs.Tadpole => "Tadpole",
        TierSlugs.Froglet => "Froglet",
        TierSlugs.FrogPlus => "Frog+",
        TierSlugs.FrogPro => "Frog Pro",
        TierSlugs.FrogLegend => "Frog Legend",
        TierSlugs.Admin => "Admin",
        _ => slug,
    };
}

/// <summary>Изолированные инстансы: instances/{id}/instance.json + своя папка .minecraft.</summary>
public sealed partial class InstanceService(EntitlementService entitlements, ILogger<InstanceService> log)
{
    public IReadOnlyList<Instance> List()
    {
        var result = new List<Instance>();
        if (!Directory.Exists(LauncherPaths.Instances)) return result;

        foreach (var dir in Directory.GetDirectories(LauncherPaths.Instances))
        {
            var file = Path.Combine(dir, "instance.json");
            if (!File.Exists(file)) continue;
            try
            {
                var inst = JsonSerializer.Deserialize<Instance>(File.ReadAllText(file), Json.Options);
                if (inst is null) continue;
                inst.Directory = dir;
                result.Add(inst);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Повреждён {File}", file);
            }
        }

        return result.OrderByDescending(i => i.LastPlayedAt ?? i.CreatedAt).ToList();
    }

    /// <summary>Создание с проверкой лимита тира (Tadpole — 1, Froglet — 5, Frog+ и выше — ∞).</summary>
    public Instance Create(string name, string gameVersion, LoaderType loader, string? loaderVersion, int ramMb)
    {
        if (entitlements.MaxInstances is { } max && List().Count >= max)
            throw new InstanceLimitException(entitlements.Tier, max);

        var id = $"{Slug(name)}-{Guid.NewGuid().ToString("N")[..6]}";
        var inst = new Instance
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? gameVersion : name.Trim(),
            GameVersion = gameVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            RamMb = ramMb,
            JvmAutotune = entitlements.IsAllowed(EntitlementKeys.JvmAutotune),
            CreatedAt = DateTimeOffset.Now,
            Directory = Path.Combine(LauncherPaths.Instances, id),
        };
        Directory.CreateDirectory(Path.Combine(inst.GameDirectory, "mods"));
        Save(inst);
        log.LogInformation("Создан инстанс {Id} ({Version})", id, inst.DisplayVersion);
        return inst;
    }

    public void Save(Instance inst)
    {
        var mods = Path.Combine(inst.GameDirectory, "mods");
        inst.Mods = Directory.Exists(mods)
            ? Directory.GetFiles(mods, "*.jar").Select(Path.GetFileName).OfType<string>().Order().ToList()
            : new List<string>();
        Directory.CreateDirectory(inst.Directory);
        File.WriteAllText(Path.Combine(inst.Directory, "instance.json"), JsonSerializer.Serialize(inst, Json.Options));
    }

    public void Delete(Instance inst)
    {
        if (Directory.Exists(inst.Directory)) Directory.Delete(inst.Directory, recursive: true);
        log.LogInformation("Удалён инстанс {Id}", inst.Id);
    }

    private static string Slug(string name)
    {
        var s = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "instance" : s[..Math.Min(24, s.Length)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
