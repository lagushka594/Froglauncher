using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;

namespace RibbixLauncher.Services;

/// <summary>
/// Проверка перед запуском — «Доктор крашей» без вылета. По метаданным модов, без запуска игры:
/// не хватает зависимости, мод под другой Minecraft, мод не для этого загрузчика, один мод дважды, мало памяти.
/// Результат — те же диагнозы и кнопки, что у Доктора, плюс «Играть всё равно».
/// </summary>
public sealed partial class PreflightService(ModToolsService tools)
{
    /// <summary>Зависимости, которые даёт сама игра или загрузчик.</summary>
    private static readonly HashSet<string> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilt_base", "forge", "neoforge", "javafml", "mixinextras",
    };

    public IReadOnlyList<CrashDiagnosis> Check(Instance inst, int maxSafeRamMb) => Check(inst, tools.List(inst).Where(m => m.Enabled).ToList(), maxSafeRamMb);

    internal static IReadOnlyList<CrashDiagnosis> Check(Instance inst, IReadOnlyList<ModInfo> mods, int maxSafeRamMb)
    {
        var result = new List<CrashDiagnosis>();
        if (inst.Loader == LoaderType.Vanilla || mods.Count == 0) return result;
        var provided = mods.SelectMany(m => m.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fabricish = inst.Loader is LoaderType.Fabric or LoaderType.Quilt;

        // Sinytra Connector запускает Fabric-моды на Forge/NeoForge.
        var connector = provided.Contains("connector") || provided.Contains("connectormod")
                        || mods.Any(m => m.Jar.StartsWith("connector-", StringComparison.OrdinalIgnoreCase));

        // Мод не для этого загрузчика (Forge-мод в Fabric и наоборот; Quilt запускает и Fabric-моды).
        foreach (var m in mods.Where(m => m.Loader.Length > 0))
        {
            var has = m.Loader.Split(',');
            var fits = inst.Loader switch
            {
                LoaderType.Fabric => has.Contains("fabric"),
                LoaderType.Quilt => has.Contains("fabric") || has.Contains("quilt"),
                // NeoForge 1.20.1 — форк Forge: моды часто подходят в обе стороны.
                LoaderType.Forge or LoaderType.NeoForge => has.Contains("forge") || has.Contains("neoforge") || connector && has.Contains("fabric"),
                _ => true,
            };
            if (!fits)
                result.Add(new CrashDiagnosis(L.T($"«{m.Name}» — мод для {string.Join(" / ", has.Select(Pretty))}"),
                    L.T($"А эта версия на {inst.Loader}. Такой мод не загрузится — выключите его или поставьте вариант под {inst.Loader}."),
                    new[] { new CrashFix(FixKind.UpdateMod, L.T($"Найти «{m.Name}» под {inst.Loader}"), m.Ids.FirstOrDefault() ?? m.Jar),
                            new CrashFix(FixKind.DisableMod, L.T($"Выключить «{m.Name}»"), "file:" + m.FileName) }));
        }

        if (fabricish)
        {
            // Не хватает зависимости.
            foreach (var m in mods.Where(m => m.Loader.Split(',').Any(x => x is "fabric" or "quilt")))
                foreach (var dep in m.Depends.Where(d => !Builtin.Contains(d) && !provided.Contains(d) && !d.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) || d == "fabric-api" && !provided.Contains(d)))
                    result.Add(new CrashDiagnosis(L.T($"Не хватает мода «{dep}»"),
                        L.T($"Его требует «{m.Name}». Без него игра не запустится."),
                        new[] { new CrashFix(FixKind.InstallMod, L.T($"Скачать «{dep}»"), dep), new CrashFix(FixKind.DisableMod, L.T($"Выключить «{m.Name}»"), "file:" + m.FileName) }));

            // Мод под другой Minecraft.
            foreach (var m in mods.Where(m => m.MinecraftRange is { Length: > 0 }))
                if (!Matches(m.MinecraftRange!, inst.GameVersion))
                    result.Add(new CrashDiagnosis(L.T($"«{m.Name}» не для {inst.GameVersion}"),
                        L.T($"Мод сделан для Minecraft {m.MinecraftRange}. Лаунчер поищет версию под {inst.GameVersion} на Modrinth."),
                        new[] { new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{m.Name}»"), m.Ids.FirstOrDefault() ?? m.Jar),
                                new CrashFix(FixKind.DisableMod, L.T($"Выключить «{m.Name}»"), "file:" + m.FileName) }));
        }

        // Один мод двумя файлами.
        foreach (var g in mods.Where(m => m.Ids.Count > 0).GroupBy(m => m.Ids[0], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            result.Add(new CrashDiagnosis(L.T($"«{g.First().Name}» установлен дважды"),
                L.T($"Файлы: {string.Join(", ", g.Select(x => x.FileName))}. Оставьте один."),
                g.Skip(1).Select(f => new CrashFix(FixKind.DisableMod, L.T($"Выключить {f.FileName}"), "file:" + f.FileName)).ToList()));

        // Много модов и мало памяти.
        var want = mods.Count switch { > 150 => 8192, > 80 => 6144, > 30 => 4096, _ => 0 };
        if (want > inst.RamMb && inst.RamMb < maxSafeRamMb)
        {
            var give = Math.Min(want, maxSafeRamMb);
            result.Add(new CrashDiagnosis(L.T($"Для {mods.Count} модов мало памяти"),
                L.T($"Сейчас {inst.RamMb / 1024.0:0.#} ГБ — с таким количеством модов игра может вылетать или долго грузиться."),
                new[] { new CrashFix(FixKind.Ram, L.T($"Дать {give / 1024.0:0.#} ГБ"), give.ToString()) }));
        }
        return result;
    }

    private static string Pretty(string loader) => loader switch { "fabric" => "Fabric", "quilt" => "Quilt", "forge" => "Forge", "neoforge" => "NeoForge", _ => loader };

    // ---------- Диапазоны версий Fabric: «1.20.1», «~1.20», «^1.20», «>=1.20 <1.21», «1.20.x», «*», списки через || ----------

    internal static bool Matches(string range, string version)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;
        var v = Parse(version);
        if (v is null) return true; // снапшоты и необычные версии не судим
        return range.Split("||").Any(alt => alt.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(c => Term(c.Trim(), v.Value)));
    }

    private static bool Term(string c, (int, int, int) v)
    {
        if (c is "*" or "") return true;
        var m = TermRegex().Match(c);
        if (!m.Success) return true; // непонятное — не мешаем запуску
        var op = m.Groups[1].Value;
        var parts = m.Groups[2].Value.Split('.');
        if (parts.Any(p => p is "x" or "X" or "*"))
        {
            var fixedParts = parts.TakeWhile(p => p is not ("x" or "X" or "*")).Select(int.Parse).ToArray();
            var arr = new[] { v.Item1, v.Item2, v.Item3 };
            return fixedParts.Select((p, i) => arr[i] == p).All(x => x);
        }
        var t = Parse(m.Groups[2].Value)!.Value;
        int cmp = Compare(v, t);
        return op switch
        {
            ">=" => cmp >= 0, ">" => cmp > 0, "<=" => cmp <= 0, "<" => cmp < 0,
            "~" => cmp >= 0 && v.Item1 == t.Item1 && v.Item2 == t.Item2,
            "^" => cmp >= 0 && v.Item1 == t.Item1 && (t.Item1 > 0 || v.Item2 == t.Item2),
            _ => parts.Length >= 3 ? cmp == 0 : v.Item1 == t.Item1 && v.Item2 == t.Item2 && (parts.Length < 3 || v.Item3 == t.Item3),
        };
    }

    private static int Compare((int, int, int) a, (int, int, int) b) => a.CompareTo(b);

    private static (int, int, int)? Parse(string s)
    {
        var m = VersionRegex().Match(s);
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    [GeneratedRegex(@"^(>=|<=|>|<|=|~|\^)?\s*(\d+(?:\.(?:\d+|x|X|\*)){0,2})(?:[-+].*)?$")]
    private static partial Regex TermRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();
}
