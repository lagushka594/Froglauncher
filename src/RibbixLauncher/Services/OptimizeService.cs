using System.IO;
using System.Net.Http;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Мод оптимизации: slug на Modrinth, id в метаданных (чтобы не ставить второй раз) и что он даёт.</summary>
public sealed record OptimizeMod(string Slug, string ModId, string Title, string What, LoaderType[] Loaders);

public sealed record OptimizeResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped, IReadOnlyList<string> Unavailable);

/// <summary>
/// «🚀 Ускорить»: ставит проверенный набор модов оптимизации под загрузчик и Minecraft версии. Перед установкой —
/// снимок папки mods («Вернуть как было»). Уже стоящие моды и альтернативы (Sodium ↔ Embeddium ↔ OptiFine) не дублирует.
/// </summary>
public sealed class OptimizeService(ModrinthService modrinth, ModToolsService tools, InstanceService instances, ILogger<OptimizeService> log)
{
    private static readonly LoaderType[] Fabricish = { LoaderType.Fabric, LoaderType.Quilt };
    private static readonly LoaderType[] All = { LoaderType.Fabric, LoaderType.Quilt, LoaderType.Forge, LoaderType.NeoForge };

    public static readonly IReadOnlyList<OptimizeMod> Mods = new[]
    {
        new OptimizeMod("sodium", "sodium", "Sodium", L.T("новый движок отрисовки — главный прирост FPS"), new[] { LoaderType.Fabric, LoaderType.Quilt, LoaderType.NeoForge }),
        new OptimizeMod("embeddium", "embeddium", "Embeddium", L.T("Sodium для Forge"), new[] { LoaderType.Forge }),
        new OptimizeMod("lithium", "lithium", "Lithium", L.T("быстрее физика, мобы и редстоун"), new[] { LoaderType.Fabric, LoaderType.Quilt, LoaderType.NeoForge }),
        new OptimizeMod("ferrite-core", "ferritecore", "FerriteCore", L.T("меньше памяти"), All),
        new OptimizeMod("immediatelyfast", "immediatelyfast", "ImmediatelyFast", L.T("быстрее интерфейс, текст и частицы"), All),
        new OptimizeMod("entityculling", "entityculling", "Entity Culling", L.T("не рисует мобов и сундуки за стенами"), All),
        new OptimizeMod("modernfix", "modernfix", "ModernFix", L.T("быстрее загрузка и меньше памяти"), new[] { LoaderType.Fabric, LoaderType.Forge, LoaderType.NeoForge }),
        new OptimizeMod("dynamic-fps", "dynamic_fps", "Dynamic FPS", L.T("не грузит ПК, когда игра свёрнута"), All),
    };

    /// <summary>Моды, которые уже делают работу Sodium.</summary>
    private static readonly string[] RenderEngines = { "sodium", "embeddium", "rubidium", "optifine", "optifabric" };

    /// <summary>Что поставить: подходящие загрузчику, без уже установленных и без второго движка отрисовки.</summary>
    internal static List<OptimizeMod> Plan(Instance inst, IReadOnlyList<ModInfo> installed, List<string> skipped)
    {
        var ids = installed.SelectMany(m => m.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = installed.Select(m => m.Jar.ToLowerInvariant()).ToList();
        var hasEngine = RenderEngines.Any(e => ids.Contains(e) || files.Any(f => f.Contains(e)));
        var plan = new List<OptimizeMod>();
        foreach (var m in Mods.Where(m => m.Loaders.Contains(inst.Loader)))
        {
            if (ids.Contains(m.ModId) || files.Any(f => f.StartsWith(m.Slug.Replace("-", "")) || f.StartsWith(m.Slug))) { skipped.Add(m.Title); continue; }
            if (m.Slug is "sodium" or "embeddium" && hasEngine) { skipped.Add(m.Title); continue; }
            plan.Add(m);
        }
        return plan;
    }

    public async Task<OptimizeResult> RunAsync(Instance inst, IProgress<string>? progress, CancellationToken ct)
    {
        if (inst.Loader == LoaderType.Vanilla)
            throw new ModsException(L.T("Моды оптимизации ставятся на Fabric, Quilt, Forge или NeoForge — создайте такую версию в «Каталоге»."));
        var skipped = new List<string>();
        var plan = Plan(inst, tools.List(inst), skipped);
        var installed = new List<string>();
        var unavailable = new List<string>();
        if (plan.Count == 0) return new OptimizeResult(installed, skipped, unavailable);

        tools.CreateSnapshot(inst, L.T("Перед «Ускорить»"));
        foreach (var m in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(L.T($"Ставим {m.Title}"));
            try
            {
                await modrinth.InstallAsync(inst, m.Slug, ct);
                installed.Add(m.Title);
            }
            catch (Exception ex) when (ex is ModsException or HttpRequestException)
            {
                // Нет сборки под эту версию Minecraft — для Sodium на NeoForge пробуем Embeddium.
                if (m.Slug == "sodium" && inst.Loader == LoaderType.NeoForge)
                {
                    try
                    {
                        await modrinth.InstallAsync(inst, "embeddium", ct);
                        installed.Add("Embeddium");
                        continue;
                    }
                    catch (Exception ex2) when (ex2 is ModsException or HttpRequestException) { }
                }
                log.LogInformation("Оптимизация {Mod} недоступна для {Version}: {Error}", m.Slug, inst.GameVersion, ex.Message);
                unavailable.Add(m.Title);
            }
        }
        instances.Save(inst);
        return new OptimizeResult(installed, skipped, unavailable);
    }
}
