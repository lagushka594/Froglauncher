using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

public sealed class PlaySession
{
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string? Server { get; set; }

    public TimeSpan Duration => (End ?? DateTimeOffset.Now) - Start;
}

public sealed class PlayLog
{
    public List<PlaySession> Sessions { get; set; } = new();
}

/// <summary>Счётчики из stats/*.json и advancements/*.json миров Minecraft.</summary>
public sealed record McStats(long PlayTicks, long Jumps, long WalkCm, long Deaths, long MobKills, int Advancements, int Worlds)
{
    public TimeSpan PlayTime => TimeSpan.FromSeconds(PlayTicks / 20.0);
}

public sealed record DayActivity(DateOnly Day, TimeSpan Time);

public sealed record PlayerBadge(string Title, string Emoji, double FromHours, double? ToHours);

/// <summary>
/// Статистика игрока: сессии, которые записывает сам лаунчер (playtime.json), плюс настоящие счётчики Minecraft
/// из папок миров всех версий. Бейдж — по общему времени в игре.
/// </summary>
public sealed class PlayStatsService(InstanceService instances, ILogger<PlayStatsService> log)
{
    private static string File => Path.Combine(LauncherPaths.Root, "playtime.json");
    private readonly object _gate = new();
    private PlaySession? _current;

    public static readonly IReadOnlyList<PlayerBadge> Badges = new[]
    {
        new PlayerBadge(L.T("Новичок"), "🌱", 0, 10),
        new PlayerBadge(L.T("Опытный"), "🐸", 10, 100),
        new PlayerBadge(L.T("Ветеран"), "⚔️", 100, 500),
        new PlayerBadge(L.T("Легенда"), "👑", 500, null),
    };

    public static PlayerBadge BadgeFor(double hours) => Badges.Last(b => hours >= b.FromHours);

    public PlayLog Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<PlayLog>(System.IO.File.ReadAllText(File), Json.Options) ?? new PlayLog();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.LogWarning(ex, "playtime.json повреждён");
        }
        return new PlayLog();
    }

    public void SessionStarted(Instance inst, string? server)
    {
        lock (_gate)
        {
            _current = new PlaySession
            {
                InstanceId = inst.Id, InstanceName = inst.Name, GameVersion = inst.GameVersion,
                Start = DateTimeOffset.Now, Server = server,
            };
        }
    }

    public PlaySession? SessionEnded()
    {
        lock (_gate)
        {
            if (_current is null) return null;
            _current.End = DateTimeOffset.Now;
            var s = _current;
            _current = null;
            if (s.Duration < TimeSpan.FromSeconds(30)) return s; // крэш на старте — не считаем
            var logFile = Load();
            logFile.Sessions.Add(s);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(logFile, Json.Options));
            return s;
        }
    }

    public PlaySession? Current => _current;

    /// <summary>Время по дням за последние N дней (сессии через полночь делятся между днями).</summary>
    public static IReadOnlyList<DayActivity> Daily(IEnumerable<PlaySession> sessions, int days, DateOnly today)
    {
        var map = Enumerable.Range(0, days).ToDictionary(i => today.AddDays(-(days - 1) + i), _ => TimeSpan.Zero);
        foreach (var s in sessions)
        {
            var start = s.Start.LocalDateTime;
            var end = (s.End ?? DateTimeOffset.Now).LocalDateTime;
            while (start < end)
            {
                var dayEnd = start.Date.AddDays(1);
                var chunkEnd = end < dayEnd ? end : dayEnd;
                var day = DateOnly.FromDateTime(start);
                if (map.ContainsKey(day)) map[day] += chunkEnd - start;
                start = chunkEnd;
            }
        }
        return map.OrderBy(kv => kv.Key).Select(kv => new DayActivity(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Сколько дней подряд (до сегодня или вчера) игрок заходил в игру.</summary>
    public static int Streak(IEnumerable<PlaySession> sessions, DateOnly today)
    {
        var days = sessions.Select(s => DateOnly.FromDateTime(s.Start.LocalDateTime)).ToHashSet();
        var d = days.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (days.Contains(d)) { streak++; d = d.AddDays(-1); }
        return streak;
    }

    /// <summary>Счётчики Minecraft из всех миров всех версий (stats/*.json, advancements/*.json).</summary>
    public McStats ReadMinecraftStats()
    {
        long ticks = 0, jumps = 0, walk = 0, deaths = 0, kills = 0;
        var advancements = 0;
        var worlds = 0;
        foreach (var inst in instances.List())
        {
            var saves = Path.Combine(inst.GameDirectory, "saves");
            if (!Directory.Exists(saves)) continue;
            foreach (var world in Directory.GetDirectories(saves))
            {
                worlds++;
                foreach (var f in SafeFiles(Path.Combine(world, "stats")))
                {
                    try
                    {
                        var custom = JsonNode.Parse(System.IO.File.ReadAllText(f))?["stats"]?["minecraft:custom"];
                        if (custom is null) continue;
                        long V(string k) => custom[k]?.GetValue<long>() ?? 0;
                        ticks += Math.Max(V("minecraft:play_time"), V("minecraft:play_one_minute")); // имя ключа сменилось в 1.17
                        jumps += V("minecraft:jump");
                        walk += V("minecraft:walk_one_cm") + V("minecraft:sprint_one_cm");
                        deaths += V("minecraft:deaths");
                        kills += V("minecraft:mob_kills");
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException) { }
                }
                foreach (var f in SafeFiles(Path.Combine(world, "advancements")))
                {
                    try
                    {
                        if (JsonNode.Parse(System.IO.File.ReadAllText(f)) is not JsonObject adv) continue;
                        // Рецепты Minecraft тоже хранит как «достижения» — их не считаем.
                        advancements += adv.Count(kv => !kv.Key.Contains(":recipes/") && kv.Value?["done"]?.GetValue<bool>() == true);
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
                }
            }
        }
        return new McStats(ticks, jumps, walk, deaths, kills, advancements, worlds);
    }

    private static IEnumerable<string> SafeFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();
}
