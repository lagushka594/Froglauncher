using System.IO;
using System.Text.Json;
using RibbixLauncher.Core;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Что известно о игроке для достижений — собирается из статистики, заданий и файлов.</summary>
public sealed record AchievementContext(
    double Hours, int Sessions, int Streak, int Servers, int Versions, int Screenshots, int Clips, int QuestLevel,
    int QuestsDone, IReadOnlyDictionary<string, int> Marks)
{
    public int Mark(string kind) => Marks.TryGetValue(kind, out var n) ? n : 0;
}

public sealed record AchievementDef(string Id, string Emoji, string Title, string Description, Func<AchievementContext, bool> Test);

public sealed record AchievementStatus(AchievementDef Def, DateTime? Unlocked);

/// <summary>
/// Достижения лаунчера (не путать с достижениями Minecraft): часы, серии, клипы, мир друзьям и т.п.
/// Открытые с датой — в achievements.json; новые показываются тостом после выхода из игры.
/// </summary>
public sealed class AchievementService(PlayStatsService stats, InstanceService instances, QuestService quests, ILogger<AchievementService> log)
{
    private static string File => Path.Combine(LauncherPaths.Root, "achievements.json");

    public static readonly IReadOnlyList<AchievementDef> All = new AchievementDef[]
    {
        new("first", "🐣", L.T("Первый прыжок"), L.T("Запустить игру через Ribbix"), c => c.Sessions >= 1),
        new("h10", "⏱", L.T("Разогрев"), L.T("10 часов в игре"), c => c.Hours >= 10),
        new("h100", "🕰", L.T("Сто часов"), L.T("100 часов в игре"), c => c.Hours >= 100),
        new("h500", "👑", L.T("Живу в Minecraft"), L.T("500 часов в игре"), c => c.Hours >= 500),
        new("streak7", "🔥", L.T("Неделя подряд"), L.T("Играть 7 дней подряд"), c => c.Streak >= 7),
        new("servers5", "🌐", L.T("Путешественник"), L.T("Зайти на 5 разных серверов"), c => c.Servers >= 5),
        new("versions5", "🔀", L.T("Коллекционер"), L.T("Поиграть в 5 разных версиях"), c => c.Versions >= 5),
        new("shots50", "📸", L.T("Фотограф"), L.T("50 скриншотов"), c => c.Screenshots >= 50),
        new("clip1", "🎬", L.T("Режиссёр"), L.T("Сделать клип для TikTok"), c => c.Clips >= 1),
        new("clip10", "🎥", L.T("Тиктокер"), L.T("10 клипов"), c => c.Clips >= 10),
        new("host", "🌍", L.T("Гостеприимный"), L.T("Открыть мир друзьям"), c => c.Mark("host") >= 1),
        new("optimize", "🚀", L.T("Турбо"), L.T("Ускорить версию"), c => c.Mark("optimize") >= 1),
        new("skin", "👕", L.T("Модник"), L.T("Надеть скин"), c => c.Mark("skin") >= 1),
        new("map", "🗺", L.T("Картограф"), L.T("Открыть карту мира"), c => c.Mark("map") >= 1),
        new("quests30", "📜", L.T("Исполнительный"), L.T("Выполнить 30 заданий"), c => c.QuestsDone >= 30),
        new("level10", "🏆", L.T("Король пруда"), L.T("10 уровень заданий"), c => c.QuestLevel >= 10),
    };

    private Dictionary<string, DateTime> LoadUnlocked()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { log.LogWarning(ex, "achievements.json повреждён"); }
        return new();
    }

    public AchievementContext Context()
    {
        var sessions = stats.Load().Sessions;
        var q = quests.Load();
        var clips = Directory.Exists(ClipService.OutputDir) ? Directory.GetFiles(ClipService.OutputDir, "*.mp4").Length : 0;
        return new AchievementContext(
            sessions.Sum(s => s.Duration.TotalHours),
            sessions.Count,
            PlayStatsService.Streak(sessions, DateOnly.FromDateTime(AppClock.Now)),
            sessions.Where(s => s.Server is { Length: > 0 }).Select(s => s.Server!.ToLowerInvariant()).Distinct().Count(),
            sessions.Select(s => s.InstanceId).Distinct().Count(),
            ShareCardService.List(instances.List()).Count,
            clips,
            q.Xp / QuestService.XpPerLevel + 1,
            q.Lifetime.GetValueOrDefault("quests"),
            q.Lifetime);
    }

    /// <summary>Проверить и запомнить новые. Возвращает только что открытые.</summary>
    public IReadOnlyList<AchievementDef> Check() => Check(Context());

    internal IReadOnlyList<AchievementDef> Check(AchievementContext ctx)
    {
        var unlocked = LoadUnlocked();
        var fresh = All.Where(a => !unlocked.ContainsKey(a.Id) && a.Test(ctx)).ToList();
        if (fresh.Count == 0) return fresh;
        foreach (var a in fresh) unlocked[a.Id] = AppClock.Now;
        Directory.CreateDirectory(LauncherPaths.Root);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(unlocked));
        return fresh;
    }

    public IReadOnlyList<AchievementStatus> List()
    {
        var unlocked = LoadUnlocked();
        return All.Select(a => new AchievementStatus(a, unlocked.TryGetValue(a.Id, out var d) ? d : null)).ToList();
    }
}
