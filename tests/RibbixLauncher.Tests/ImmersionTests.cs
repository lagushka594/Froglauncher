using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace RibbixLauncher.Tests;

public class PlayStatsTests
{
    private static PlaySession S(DateTime start, TimeSpan len) => new()
    {
        InstanceId = "a", Start = new DateTimeOffset(start), End = new DateTimeOffset(start + len),
    };

    [Fact]
    public void Session_over_midnight_is_split_between_days()
    {
        var today = new DateOnly(2026, 10, 27);
        var days = PlayStatsService.Daily(new[] { S(new DateTime(2026, 10, 26, 23, 0, 0), TimeSpan.FromHours(2)) }, 3, today);
        Assert.Equal(3, days.Count);
        Assert.Equal(TimeSpan.Zero, days[0].Time);
        Assert.Equal(TimeSpan.FromHours(1), days[1].Time);
        Assert.Equal(TimeSpan.FromHours(1), days[2].Time);
        Assert.Equal(today, days[2].Day);
    }

    [Fact]
    public void Sessions_outside_window_are_ignored()
    {
        var today = new DateOnly(2026, 10, 27);
        var days = PlayStatsService.Daily(new[] { S(new DateTime(2026, 9, 1, 12, 0, 0), TimeSpan.FromHours(3)) }, 14, today);
        Assert.All(days, d => Assert.Equal(TimeSpan.Zero, d.Time));
    }

    [Fact]
    public void Streak_counts_consecutive_days_up_to_yesterday()
    {
        var today = new DateOnly(2026, 10, 27);
        var s = new[]
        {
            S(new DateTime(2026, 10, 26, 18, 0, 0), TimeSpan.FromHours(1)),
            S(new DateTime(2026, 10, 25, 18, 0, 0), TimeSpan.FromHours(1)),
            S(new DateTime(2026, 10, 23, 18, 0, 0), TimeSpan.FromHours(1)), // разрыв 24-го
        };
        Assert.Equal(2, PlayStatsService.Streak(s, today));
        Assert.Equal(0, PlayStatsService.Streak(s, today.AddDays(2)));
    }

    [Theory]
    [InlineData(0, "Новичок")]
    [InlineData(9.9, "Новичок")]
    [InlineData(10, "Опытный")]
    [InlineData(150, "Ветеран")]
    [InlineData(5000, "Легенда")]
    public void Badge_by_hours(double hours, string title) => Assert.Equal(title, PlayStatsService.BadgeFor(hours).Title);
}

public class WorldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frog-worlds-" + Guid.NewGuid().ToString("N"));
    private readonly Instance _inst;

    public WorldTests()
    {
        _inst = new Instance { Id = "zz-test", Name = "Тест", Directory = _root };
        var world = Path.Combine(_inst.GameDirectory, "saves", "w1");
        Directory.CreateDirectory(Path.Combine(world, "region"));
        File.WriteAllBytes(Path.Combine(world, "level.dat"), LevelDat("Мой мир", gameType: 1, lastPlayed: 1_760_000_000_000));
        File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "v1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Nbt_reads_level_dat()
    {
        var path = Path.Combine(_inst.GameDirectory, "saves", "w1", "level.dat");
        var data = (Dictionary<string, object?>)Nbt.ReadGzip(path)!["Data"]!;
        Assert.Equal("Мой мир", data["LevelName"]);
        Assert.Equal(1, data["GameType"]);
        Assert.Equal(1_760_000_000_000L, data["LastPlayed"]);
        Assert.Equal("1.21.4", ((Dictionary<string, object?>)data["Version"]!)["Name"]);
    }

    [Fact]
    public void List_shows_world_name_and_mode()
    {
        var w = Assert.Single(new WorldService(NullLogger<WorldService>.Instance).List(_inst));
        Assert.Equal("Мой мир", w.Name);
        Assert.Contains("Творчество", w.Summary);
    }

    [Fact]
    public void Backup_and_restore_round_trip_keeps_safety_copy()
    {
        var svc = new WorldService(NullLogger<WorldService>.Instance);
        var region = Path.Combine(_inst.GameDirectory, "saves", "w1", "region", "r.0.0.mca");
        var backup = svc.Backup(_inst, "w1");

        File.WriteAllText(region, "v2-broken");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(region)!, "junk.mca"), "x");
        svc.Restore(_inst, "w1", backup);

        Assert.Equal("v1", File.ReadAllText(region));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(region)!, "junk.mca")));
        Assert.Contains(svc.Backups(_inst, "w1"), b => b.Path.EndsWith("-before-restore.zip"));
    }

    // ---------- Мини-писатель NBT для тестов ----------
    private static byte[] LevelDat(string name, int gameType, long lastPlayed)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        Tag(w, 10, "");
        Tag(w, 10, "Data");
        Tag(w, 8, "LevelName"); Str(w, name);
        Tag(w, 3, "GameType"); Int(w, gameType);
        Tag(w, 4, "LastPlayed"); Long(w, lastPlayed);
        Tag(w, 1, "Difficulty"); w.Write((byte)2);
        Tag(w, 10, "Version");
        Tag(w, 8, "Name"); Str(w, "1.21.4");
        w.Write((byte)0); // конец Version
        Tag(w, 9, "ServerBrands"); w.Write((byte)8); Int(w, 1); Str(w, "fabric");
        w.Write((byte)0); // конец Data
        w.Write((byte)0); // конец корня

        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) z.Write(ms.ToArray());
        return gz.ToArray();
    }

    private static void Tag(BinaryWriter w, byte type, string name) { w.Write(type); Str(w, name); }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)b.Length);
        w.Write(len);
        w.Write(b);
    }

    private static void Int(BinaryWriter w, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); w.Write(b); }
    private static void Long(BinaryWriter w, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); w.Write(b); }
}
