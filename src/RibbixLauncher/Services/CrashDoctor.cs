using System.IO;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;

namespace RibbixLauncher.Services;

public enum FixKind { InstallMod, UpdateMod, DisableMod, Java, Ram, Rollback, Bisect, Drivers, LaunchAnyway }

/// <summary>Кнопка починки. Arg — modId, мажорная версия Java или мегабайты памяти.</summary>
public sealed record CrashFix(FixKind Kind, string Label, string? Arg = null);

public sealed record CrashDiagnosis(string Title, string Explanation, IReadOnlyList<CrashFix> Fixes, string? Evidence = null);

/// <summary>
/// «Доктор крашей»: разбирает crash-report и лог и сопоставляет с известными причинами — не хватает зависимости,
/// мод не под эту версию, не та Java, мало памяти, моды конфликтуют, дубликаты, драйвер видеокарты.
/// Только чтение текста: сами починки выполняет <see cref="ViewModels.CrashDoctorViewModel"/>.
/// </summary>
public static partial class CrashDoctor
{
    public sealed record Context(Instance Instance, IReadOnlyList<ModInfo> Mods, int MaxSafeRamMb, bool HasPendingSnapshot);

    public static string ReadText(CrashInfo crash)
    {
        var parts = new List<string>();
        if (crash.CrashReportPath is { } p && File.Exists(p))
            try { parts.Add(File.ReadAllText(p)); } catch (IOException) { }
        parts.Add(crash.Text);
        return string.Join("\n", parts);
    }

    public static IReadOnlyList<CrashDiagnosis> Diagnose(string text, Context ctx)
    {
        var result = new List<CrashDiagnosis>();
        var seen = new HashSet<string>();
        void Add(CrashDiagnosis d)
        {
            if (seen.Add(d.Title)) result.Add(d);
        }
        string Name(string id) => ctx.Mods.FirstOrDefault(m => m.Ids.Contains(id, StringComparer.OrdinalIgnoreCase))?.Name ?? id;
        var mc = ctx.Instance.GameVersion;

        if (ctx.HasPendingSnapshot)
            Add(new CrashDiagnosis(L.T("Игра упала после обновления модов"),
                L.T("Перед «Обновить всё» лаунчер сохранил снимок папки mods. Верните её как было — одной кнопкой."),
                new[] { new CrashFix(FixKind.Rollback, L.T("↩ Вернуть моды как было")) }));

        // Fabric/Quilt: «Mod 'A' (a) 1.0 requires ... of mod 'B' (b), which is missing!»
        foreach (Match m in FabricMissingRegex().Matches(text))
        {
            var (src, srcId, dep, depId) = (m.Groups["name"].Value, m.Groups["id"].Value, Or(m.Groups["dname"].Value, m.Groups["did"].Value), m.Groups["did"].Value);
            Add(Missing(src, srcId, dep, depId));
        }
        // Fabric/Quilt: «... requires version X of 'B' (b), but only the wrong version is present: Y!»
        foreach (Match m in FabricWrongRegex().Matches(text))
            Add(WrongVersion(m.Groups["name"].Value, m.Groups["id"].Value, m.Groups["dname"].Value, m.Groups["did"].Value,
                m.Groups["req"].Value.Trim(), m.Groups["have"].Value.Trim(), mc));
        foreach (Match m in FabricIncompatibleRegex().Matches(text))
        {
            var (a, aId, b, bId) = (m.Groups["name"].Value, m.Groups["id"].Value, m.Groups["dname"].Value, m.Groups["did"].Value);
            Add(new CrashDiagnosis(L.T($"«{a}» и «{b}» конфликтуют"),
                L.T($"Эти моды не работают вместе. Выключите один из них."),
                new[] { new CrashFix(FixKind.DisableMod, L.T($"Выключить «{a}»"), aId), new CrashFix(FixKind.DisableMod, L.T($"Выключить «{b}»"), bId) },
                m.Value));
        }

        // Forge/NeoForge: «Mod ID: 'b', Requested by: 'a', Expected range: '[1,)', Actual version: '[MISSING]'»
        foreach (Match m in ForgeDependencyRegex().Matches(text))
        {
            var (depId, srcId, range, actual) = (m.Groups["id"].Value, m.Groups["by"].Value, m.Groups["range"].Value, m.Groups["actual"].Value);
            Add(actual.Contains("MISSING", StringComparison.OrdinalIgnoreCase)
                ? Missing(Name(srcId), srcId, Name(depId), depId)
                : WrongVersion(Name(srcId), srcId, Name(depId), depId, range, actual, mc));
        }

        // Дубликаты: один и тот же мод двумя файлами.
        foreach (var group in ctx.Mods.Where(x => x.Enabled && x.Ids.Count > 0).GroupBy(x => x.Ids[0], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            if (DuplicateRegex().IsMatch(text) || text.Contains(group.Key, StringComparison.OrdinalIgnoreCase))
            {
                var files = group.ToList();
                Add(new CrashDiagnosis(L.T($"«{files[0].Name}» установлен дважды"),
                    L.T($"В папке mods {files.Count} файла одного мода: {string.Join(", ", files.Select(f => f.FileName))}. Оставьте один."),
                    files.Skip(1).Select(f => new CrashFix(FixKind.DisableMod, L.T($"Выключить {f.FileName}"), "file:" + f.FileName)).ToList()));
            }

        // Java: класс собран под более новую Java.
        if (ClassVersionRegex().Match(text) is { Success: true } cv)
        {
            var major = int.Parse(cv.Groups[1].Success ? cv.Groups[1].Value : cv.Groups[2].Value) - 44;
            Add(JavaFix(major, L.T($"Игре или моду нужна Java {major}, а запущена более старая.")));
        }
        if (text.Contains("cannot be cast to class java.net.URLClassLoader", StringComparison.Ordinal))
            Add(JavaFix(8, L.T("Этот Forge работает только на Java 8, а запущена новее.")));

        // Память.
        if (text.Contains("java.lang.OutOfMemoryError", StringComparison.Ordinal))
        {
            var current = ctx.Instance.RamMb;
            var next = Math.Min(ctx.MaxSafeRamMb, Math.Max(current + 2048, 4096));
            Add(new CrashDiagnosis(L.T("Игре не хватило памяти"),
                next > current
                    ? L.T($"Сейчас игре выделено {current / 1024.0:0.#} ГБ — для этой сборки мало.")
                    : L.T($"Игре уже выделено {current / 1024.0:0.#} ГБ — больше на этом ПК не стоит. Уберите тяжёлые моды или уменьшите дальность прорисовки."),
                next > current ? new[] { new CrashFix(FixKind.Ram, L.T($"Дать {next / 1024.0:0.#} ГБ"), next.ToString()) } : Array.Empty<CrashFix>()));
        }
        if (HeapRegex().IsMatch(text))
        {
            var lower = Math.Max(1024, Math.Min(ctx.Instance.RamMb - 2048, ctx.MaxSafeRamMb));
            Add(new CrashDiagnosis(L.T("Java не может выделить столько памяти"),
                L.T($"Выделено {ctx.Instance.RamMb / 1024.0:0.#} ГБ — больше, чем Windows может дать игре сейчас."),
                new[] { new CrashFix(FixKind.Ram, L.T($"Поставить {lower / 1024.0:0.#} ГБ"), lower.ToString()) }));
        }

        // Видеокарта.
        if (DriverRegex().Match(text) is { Success: true } gl)
            Add(new CrashDiagnosis(L.T("Проблема с драйвером видеокарты"),
                L.T("Игра не смогла запустить OpenGL. Обычно помогает свежий драйвер с сайта производителя видеокарты."),
                new[] { new CrashFix(FixKind.Drivers, L.T("Скачать драйвер")) }, gl.Value));

        // Подозреваемый мод из crash-report или mixin.
        if (result.Count == 0 || result.All(r => r.Fixes.All(f => f.Kind == FixKind.Rollback)))
            foreach (var id in Suspects(text, ctx).Take(2))
                Add(new CrashDiagnosis(L.T($"Похоже, виноват «{Name(id)}»"),
                    L.T("Игра упала внутри этого мода. Обновите его или выключите и попробуйте снова."),
                    new[] { new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{Name(id)}»"), id), new CrashFix(FixKind.DisableMod, L.T($"Выключить «{Name(id)}»"), id) }));

        var enabledMods = ctx.Mods.Count(x => x.Enabled);
        if (result.All(r => r.Fixes.All(f => f.Kind == FixKind.Rollback)))
            Add(new CrashDiagnosis(L.T("Точную причину не узнали"),
                enabledMods >= 2
                    ? L.T("Лаунчер может сам найти мод-виновника: выключит половину модов, проверит запуск и так дальше — из 100 модов примерно за 7 запусков.")
                    : L.T("Загрузите лог на mclo.gs и покажите его в нашем Discord — поможем."),
                enabledMods >= 2 ? new[] { new CrashFix(FixKind.Bisect, L.T("🔎 Найти мод-виновника")) } : Array.Empty<CrashFix>()));
        else if (enabledMods >= 2 && result.Any(r => r.Fixes.Any(f => f.Kind == FixKind.DisableMod)) && !result.Any(r => r.Title == L.T("Точную причину не узнали")))
            result.Add(new CrashDiagnosis(L.T("Не помогло?"), L.T("Лаунчер сам найдёт мод, из-за которого игра падает при запуске."),
                new[] { new CrashFix(FixKind.Bisect, L.T("🔎 Найти мод-виновника")) }));
        return result;

        CrashDiagnosis Missing(string src, string srcId, string dep, string depId) => new(
            L.T($"Не хватает мода «{dep}»"),
            L.T($"Его требует «{src}». Лаунчер скачает его с Modrinth под {mc}."),
            new[] { new CrashFix(FixKind.InstallMod, L.T($"Скачать «{dep}»"), depId), new CrashFix(FixKind.DisableMod, L.T($"Выключить «{src}»"), srcId) });
    }

    private static CrashDiagnosis WrongVersion(string src, string srcId, string dep, string depId, string req, string have, string mc)
    {
        if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            return new CrashDiagnosis(L.T($"«{src}» не работает на {have}"),
                L.T($"Этот файл мода сделан для Minecraft {req}. Лаунчер поищет версию под {mc} на Modrinth."),
                new[] { new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{src}»"), srcId), new CrashFix(FixKind.DisableMod, L.T($"Выключить «{src}»"), srcId) });
        if (depId.Equals("java", StringComparison.OrdinalIgnoreCase) && DigitsRegex().Match(req) is { Success: true } j)
            return JavaFix(int.Parse(j.Value), L.T($"«{src}» требует Java {req}, а запущена {have}."));
        if (depId is "fabricloader" or "quilt_loader" or "forge" or "neoforge")
            return new CrashDiagnosis(L.T($"«{src}» нужен загрузчик новее"),
                L.T($"Нужна версия {dep} {req}, а стоит {have}. Создайте версию с новым загрузчиком в «Каталоге» или поставьте старую версию мода."),
                new[] { new CrashFix(FixKind.DisableMod, L.T($"Выключить «{src}»"), srcId) });
        return new CrashDiagnosis(L.T($"«{src}» нужна другая версия «{dep}»"),
            L.T($"Нужна {req}, а стоит {have}. Обновите оба мода."),
            new[] { new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{dep}»"), depId), new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{src}»"), srcId) });
    }

    private static CrashDiagnosis JavaFix(int major, string why) => new(
        L.T($"Нужна Java {major}"), why + " " + L.T("Лаунчер скачает её и запомнит для этой версии."),
        new[] { new CrashFix(FixKind.Java, L.T($"Скачать Java {major}"), major.ToString()) });

    /// <summary>Моды, в которых упала игра: «Suspected Mod(s)» в отчёте, mixin-конфиг, «-- MOD x --».</summary>
    internal static IEnumerable<string> Suspects(string text, Context ctx)
    {
        var ids = new List<string>();
        foreach (Match m in SuspectedRegex().Matches(text)) ids.Add(m.Groups[1].Value);
        foreach (Match m in MixinRegex().Matches(text)) ids.Add(m.Groups[1].Value);
        foreach (Match m in ForgeModSectionRegex().Matches(text)) ids.Add(m.Groups[1].Value);
        var known = ctx.Mods.SelectMany(x => x.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Where(id => !ModToolsService.CoreLibraries.Contains(id) && (known.Count == 0 || known.Contains(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Or(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    [GeneratedRegex(@"Mod '(?<name>[^']+)' \((?<id>[^)]+)\) \S+ requires .*? of (?:mod )?(?:'(?<dname>[^']+)' \((?<did>[^)]+)\)|(?<did>[\w\-]+)), which is missing")]
    private static partial Regex FabricMissingRegex();

    [GeneratedRegex(@"Mod '(?<name>[^']+)' \((?<id>[^)]+)\) \S+ requires (?:version )?(?<req>.+?) of (?:mod )?'(?<dname>[^']+)' \((?<did>[^)]+)\), but only the wrong version is present: (?<have>[^!\r\n]+)!")]
    private static partial Regex FabricWrongRegex();

    [GeneratedRegex(@"Mod '(?<name>[^']+)' \((?<id>[^)]+)\) \S+ is incompatible with .*? of (?:mod )?'(?<dname>[^']+)' \((?<did>[^)]+)\)")]
    private static partial Regex FabricIncompatibleRegex();

    [GeneratedRegex(@"Mod ID: '(?<id>[^']+)', Requested by: '(?<by>[^']+)', Expected range: '(?<range>[^']+)', Actual version: '(?<actual>[^']+)'")]
    private static partial Regex ForgeDependencyRegex();

    [GeneratedRegex(@"(?i)duplicate mod|multiple versions|DuplicateModsFoundException|found \d+ mods with the id")]
    private static partial Regex DuplicateRegex();

    [GeneratedRegex(@"class file version (\d+)\.\d+|Unsupported class file major version (\d+)")]
    private static partial Regex ClassVersionRegex();

    [GeneratedRegex(@"Could not reserve enough space for|Invalid maximum heap size|Initial heap size set to a larger value")]
    private static partial Regex HeapRegex();

    [GeneratedRegex(@"Pixel format not accelerated|GLFW error 6554[23]|WGL: The driver does not appear to support OpenGL|Couldn't set pixel format|EXCEPTION_ACCESS_VIOLATION[\s\S]{0,400}?(?:atio6axx|nvoglv64|ig\w*icd64)\.dll")]
    private static partial Regex DriverRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"Suspected Mods?:\s*[^\n(]*\(([\w\-]+)\)")]
    private static partial Regex SuspectedRegex();

    [GeneratedRegex(@"(?:Mixin apply for mod |from mod )([\w\-]+)")]
    private static partial Regex MixinRegex();

    [GeneratedRegex(@"-- MOD ([\w\-]+) --")]
    private static partial Regex ForgeModSectionRegex();
}
