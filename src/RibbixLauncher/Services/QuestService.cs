using System.IO;
using System.Text.Json;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Шаблон задания: Kind — как считается прогресс, Goal — сколько нужно, Xp — награда.</summary>
public sealed record QuestDef(string Id, string Kind, int Goal, int Xp, string Title, string Emoji);

public sealed record QuestProgress(QuestDef Def, int Value, bool Done)
{
    public double Percent => Math.Min(100, Value * 100.0 / Def.Goal);
}

public sealed class QuestState
{
    public string Day { get; set; } = "";
    public List<string> Claimed { get; set; } = new();
    public int Xp { get; set; }
    /// <summary>События дня (надел скин, ускорил версию, замерил FPS…): вид → сколько раз сегодня.</summary>
    public Dictionary<string, int> Marks { get; set; } = new();
}

/// <summary>
/// Задания дня: три из пула на каждый день (одинаковые весь день), прогресс — по сессиям, скриншотам и событиям
/// лаунчера. Опыт копится в уровни; уровни 5 и 10 открывают эксклюзивные темы. Всё локально, без сервера.
/// </summary>
public sealed class QuestService(PlayStatsService stats, InstanceService instances, ILogger<QuestService> log)
{
    public const int XpPerLevel = 300;

    /// <summary>Для мест вне DI (окно карты, настройки версии): отметить событие задания.</summary>
    public static void MarkGlobal(string kind) => _current?.Mark(kind);
    private static QuestService? _current;
    private static string File => Path.Combine(LauncherPaths.Root, "quests.json");

    public static readonly IReadOnlyList<QuestDef> Pool = new[]
    {
        new QuestDef("play30", "play", 30, 60, L.T("Сыграй 30 минут"), "⏱"),
        new QuestDef("play60", "play", 60, 100, L.T("Сыграй час"), "⏱"),
        new QuestDef("server", "server", 1, 80, L.T("Зайди на сервер"), "🌐"),
        new QuestDef("versions", "versions", 2, 70, L.T("Поиграй в 2 разные версии"), "🔀"),
        new QuestDef("screenshot", "screenshot", 1, 50, L.T("Сделай скриншот в игре (F2)"), "📸"),
        new QuestDef("content", "content", 1, 60, L.T("Поставь мод, ресурспак или шейдер"), "🧩"),
        new QuestDef("skin", "skin", 1, 50, L.T("Надень скин"), "👕"),
        new QuestDef("fps", "fps", 1, 70, L.T("Замерь FPS в настройках версии"), "📈"),
        new QuestDef("map", "map", 1, 40, L.T("Открой карту мира"), "🗺"),
        new QuestDef("sessions", "sessions", 3, 80, L.T("Запусти игру 3 раза"), "▶"),
        new QuestDef("clip", "clip", 1, 80, L.T("Сделай клип для TikTok"), "🎬"),
    };

    /// <summary>Уровень заданий — для тем, которые открываются за уровни (ThemeService).</summary>
    public int Level => Load().Xp / XpPerLevel + 1;

    public QuestState Load()
    {
        _current = this; // главная вызывает Load при старте — дальше MarkGlobal работает
        try
        {
            if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<QuestState>(System.IO.File.ReadAllText(File), Json.Options) ?? new QuestState();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { log.LogWarning(ex, "quests.json повреждён"); }
        return new QuestState();
    }

    private void Save(QuestState s)
    {
        Directory.CreateDirectory(LauncherPaths.Root);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s, Json.Options));
    }

    private static string Today => DateOnly.FromDateTime(AppClock.Now).ToString("yyyy-MM-dd");

    /// <summary>Три задания дня: одинаковые весь день, без повторов вида.</summary>
    internal static IReadOnlyList<QuestDef> ForDay(DateOnly day)
    {
        var rng = new Random(day.DayNumber * 7919);
        return Pool.OrderBy(_ => rng.Next()).DistinctBy(q => q.Kind).Take(3).ToList();
    }

    /// <summary>Событие для заданий (надел скин, замерил FPS, открыл карту…).</summary>
    public void Mark(string kind)
    {
        var s = Fresh(Load());
        s.Marks[kind] = s.Marks.GetValueOrDefault(kind) + 1;
        Save(s);
    }

    private static QuestState Fresh(QuestState s)
    {
        if (s.Day == Today) return s;
        s.Day = Today;
        s.Claimed.Clear();
        s.Marks.Clear();
        return s;
    }

    /// <summary>Прогресс заданий дня; выполненные сразу начисляют опыт (один раз). Возвращает и только что выполненные.</summary>
    public (IReadOnlyList<QuestProgress> Quests, IReadOnlyList<QuestDef> JustDone, int Xp) Refresh()
    {
        var s = Fresh(Load());
        var today = DateOnly.FromDateTime(AppClock.Now);
        var sessions = stats.Load().Sessions.Where(x => DateOnly.FromDateTime(x.Start.LocalDateTime) == today).ToList();
        int Value(QuestDef q) => q.Kind switch
        {
            "play" => (int)sessions.Sum(x => x.Duration.TotalMinutes),
            "server" => sessions.Count(x => x.Server is { Length: > 0 }),
            "versions" => sessions.Select(x => x.InstanceId).Distinct().Count(),
            "sessions" => sessions.Count,
            "screenshot" => ShareCardService.List(instances.List()).Count(x => DateOnly.FromDateTime(x.Taken) == today),
            "content" => s.Marks.GetValueOrDefault("content") + CountNewContent(today),
            _ => s.Marks.GetValueOrDefault(q.Kind),
        };
        var list = new List<QuestProgress>();
        var justDone = new List<QuestDef>();
        foreach (var q in ForDay(today))
        {
            var v = Math.Min(q.Goal, Value(q));
            var done = v >= q.Goal;
            if (done && !s.Claimed.Contains(q.Id))
            {
                s.Claimed.Add(q.Id);
                s.Xp += q.Xp;
                justDone.Add(q);
            }
            list.Add(new QuestProgress(q, v, done));
        }
        Save(s);
        return (list, justDone, s.Xp);
    }

    /// <summary>Файлы модов, ресурспаков и шейдеров, появившиеся сегодня (поставлены из каталога или вручную).</summary>
    private int CountNewContent(DateOnly today)
    {
        var n = 0;
        foreach (var inst in instances.List())
            foreach (var folder in new[] { "mods", "resourcepacks", "shaderpacks" })
            {
                var dir = Path.Combine(inst.GameDirectory, folder);
                if (!Directory.Exists(dir)) continue;
                n += new DirectoryInfo(dir).EnumerateFiles().Count(f => DateOnly.FromDateTime(f.CreationTime) == today);
            }
        return Math.Min(n, 1);
    }
}
