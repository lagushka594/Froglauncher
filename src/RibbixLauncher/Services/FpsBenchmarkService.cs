using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed record FpsSample(SettingsPreset Preset, double Average, double Low)
{
    public bool Meets(int target) => Low >= target * 0.95;
}

public sealed record FpsResult(SettingsPreset? Best, IReadOnlyList<FpsSample> Runs, string Message);

public sealed class FpsBenchmarkException(string message) : Exception(message);

/// <summary>
/// Настройка графики по реальному замеру: копия последнего мира (только регионы вокруг игрока),
/// запуск с --quickPlaySingleplayer, Java-агент печатает настоящий FPS игры раз в секунду.
/// Перебираем пресеты «Подобрать под мой ПК», пока не найдём самый красивый, который держит цель.
/// options.txt игрока восстанавливается, мир-копия удаляется.
/// </summary>
public sealed class FpsBenchmarkService(LaunchService launcher, IHttpClientFactory httpFactory, ILogger<FpsBenchmarkService> log)
{
    public const string BenchWorld = "RibbixLauncher-FPS-test";
    public const int WarmupSeconds = 6, MeasureSeconds = 18, MaxRuns = 3;
    /// <summary>Строка агента в логе игры: «RIBBIXFPS 60».</summary>
    public const string FpsMarker = "RIBBIXFPS ";
    public static TimeSpan JoinTimeout { get; set; } = TimeSpan.FromMinutes(4);

    public bool IsRunning { get; private set; }

    public async Task<FpsResult> RunAsync(Instance inst, GameAccount account, IReadOnlyList<SettingsPreset> presets, PresetKind start, int target,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException(L.T("Замер уже идёт"));
        IsRunning = true;
        var optionsPath = Path.Combine(inst.GameDirectory, "options.txt");
        var originalOptions = File.Exists(optionsPath) ? await File.ReadAllBytesAsync(optionsPath, ct) : null;
        var benchDir = Path.Combine(inst.GameDirectory, "saves", BenchWorld);
        try
        {
            if (!await SupportsQuickPlayAsync(inst, ct))
                throw new FpsBenchmarkException(L.T("Замер работает на Minecraft 1.20 и новее (нужен быстрый вход в мир)."));
            progress?.Report(L.T("Готовим счётчик FPS"));
            var (cls, field) = await ResolveFpsFieldAsync(inst, ct);
            var agent = ExtractAgent();
            progress?.Report(L.T("Копируем мир для теста"));
            PrepareWorld(inst, benchDir);

            var runs = new List<FpsSample>();
            var order = presets.ToList();
            var i = Math.Max(0, order.FindIndex(p => p.Kind == start));
            var wentDown = false;
            while (runs.Count < MaxRuns && i >= 0 && i < order.Count && runs.All(r => r.Preset != order[i]))
            {
                var p = order[i];
                progress?.Report(L.T($"Замер {runs.Count + 1}: «{p.Title}» — игра откроется сама, не трогайте её"));
                WriteOptions(optionsPath, originalOptions, p);
                var samples = await MeasureAsync(inst, account, agent, cls, field, ct);
                var sample = new FpsSample(p, samples.Average(), Percentile(samples, 0.1));
                runs.Add(sample);
                log.LogInformation("FPS {Preset}: среднее {Avg:0}, 10% {Low:0}", p.Kind, sample.Average, sample.Low);
                if (!sample.Meets(target)) { i--; wentDown = true; }
                else if (!wentDown && sample.Low > target * 1.5) i++;
                else break;
            }
            var best = runs.Where(r => r.Meets(target)).MaxBy(r => order.IndexOf(r.Preset));
            var msg = best is not null
                ? L.T($"«{best.Preset.Title}» держит {target} FPS: в среднем {best.Average:0}, в худшие моменты {best.Low:0}.")
                : L.T($"Даже «{order[0].Title}» не держит {target} FPS (в среднем {runs.Last().Average:0}). Поставьте Sodium или выберите цель пониже.");
            return new FpsResult(best?.Preset ?? order[0], runs, msg);
        }
        finally
        {
            if (originalOptions is not null) await File.WriteAllBytesAsync(optionsPath, originalOptions, CancellationToken.None);
            else if (File.Exists(optionsPath)) File.Delete(optionsPath);
            try { if (Directory.Exists(benchDir)) Directory.Delete(benchDir, true); }
            catch (IOException ex) { log.LogWarning(ex, "Не удалить тестовый мир"); }
            IsRunning = false;
        }
    }

    internal static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Clamp(Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }

    private static async Task<bool> SupportsQuickPlayAsync(Instance inst, CancellationToken ct)
    {
        var json = LauncherPaths.VersionJson(inst.GameVersion);
        return File.Exists(json) && (await File.ReadAllTextAsync(json, ct)).Contains("quickPlaySingleplayer", StringComparison.Ordinal);
    }

    /// <summary>Пресет поверх настроек игрока: графика, дальность, без лимита FPS и V-Sync — чтобы мерить запас.</summary>
    private static void WriteOptions(string path, byte[]? original, SettingsPreset p)
    {
        if (original is not null) File.WriteAllBytes(path, original);
        var opt = OptionsFile.Load(Path.GetDirectoryName(path)!);
        if (opt.Has("fancyGraphics") && !opt.Has("graphicsMode")) opt.Set("fancyGraphics", p.Graphics > 0 ? "true" : "false");
        else opt.Set("graphicsMode", p.Graphics.ToString());
        opt.Set("renderDistance", p.RenderDistance.ToString());
        opt.Set("simulationDistance", Math.Max(5, p.SimulationDistance).ToString());
        opt.Set("particles", p.Particles.ToString());
        opt.Set("maxFps", McValues.UnlimitedFps.ToString());
        opt.Set("enableVsync", "false");
        opt.Set("pauseOnLostFocus", "false");
        opt.Set("onboardAccessibility", "false");
        opt.Set("skipMultiplayerWarning", "true");
        opt.Save();
    }

    private async Task<List<double>> MeasureAsync(Instance inst, GameAccount account, string agent, string cls, string field, CancellationToken ct)
    {
        var samples = new List<double>();
        var joined = DateTime.MaxValue;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (line.Contains("joined the game", StringComparison.Ordinal) && joined == DateTime.MaxValue) joined = DateTime.UtcNow;
            var i = line.IndexOf(FpsMarker, StringComparison.Ordinal);
            if (i < 0 || DateTime.UtcNow - joined < TimeSpan.FromSeconds(WarmupSeconds)) return;
            if (int.TryParse(line.AsSpan(i + FpsMarker.Length).Trim(), out var fps))
                lock (samples)
                {
                    samples.Add(fps);
                    if (samples.Count >= MeasureSeconds) done.TrySetResult();
                }
        }
        void OnExit(GameSession s, CrashInfo? _) => exited.TrySetResult();
        launcher.OutputReceived += OnLine;
        launcher.Exited += OnExit;
        var started = false;
        try
        {
            await launcher.LaunchAsync(inst, account, autotune: false, null, ct, extras: new LaunchExtras(true,
                new[] { $"-javaagent:{agent}={cls}:{field}" }, new[] { "--quickPlaySingleplayer", BenchWorld }));
            started = true;
            var first = await Task.WhenAny(done.Task, exited.Task, Task.Delay(JoinTimeout + TimeSpan.FromSeconds(WarmupSeconds + MeasureSeconds), ct));
            ct.ThrowIfCancellationRequested();
            if (first == exited.Task) throw new FpsBenchmarkException(L.T("Игра закрылась во время замера — проверьте, запускается ли она обычной кнопкой «Играть»."));
            lock (samples)
                return samples.Count > 0 ? samples.ToList() : throw new FpsBenchmarkException(L.T("Не дождались входа в мир — попробуйте ещё раз."));
        }
        finally
        {
            if (started)
            {
                launcher.Stop();
                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            }
            launcher.OutputReceived -= OnLine;
            launcher.Exited -= OnExit;
        }
    }

    private static string ExtractAgent()
    {
        var path = Path.Combine(LauncherPaths.Cache, "ribbix-fps-agent.jar");
        Directory.CreateDirectory(LauncherPaths.Cache);
        using var res = typeof(FpsBenchmarkService).Assembly.GetManifestResourceStream("RibbixLauncher.ribbix-fps-agent.jar")!;
        using (var fs = File.Create(path)) res.CopyTo(fs);
        return path;
    }

    // ---------- Тестовый мир ----------

    /// <summary>Копия самого свежего мира: level.dat и регионы 3×3 вокруг игрока — быстро даже для огромных миров.</summary>
    internal static void PrepareWorld(Instance inst, string benchDir)
    {
        var saves = Path.Combine(inst.GameDirectory, "saves");
        var source = Directory.Exists(saves)
            ? Directory.GetDirectories(saves).Where(d => Path.GetFileName(d) != BenchWorld && File.Exists(Path.Combine(d, "level.dat")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "level.dat"))).FirstOrDefault()
            : null;
        if (source is null) throw new FpsBenchmarkException(L.T("Нужен хотя бы один одиночный мир: зайдите в игру, создайте мир — и запустите замер снова."));
        if (Directory.Exists(benchDir)) Directory.Delete(benchDir, true);
        Directory.CreateDirectory(benchDir);
        var (rx, rz) = PlayerRegion(Path.Combine(source, "level.dat"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (!KeepFile(rel, rx, rz)) continue;
            var dst = Path.Combine(benchDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst);
        }
    }

    /// <summary>Только Верхний мир рядом с игроком; без других измерений, бэкапов, статистики и session.lock.</summary>
    internal static bool KeepFile(string rel, int rx, int rz)
    {
        var parts = rel.Replace('\\', '/').Split('/');
        if (parts[^1] == "session.lock") return false;
        if (parts.Length == 1) return true;
        if (parts[0] is "DIM-1" or "DIM1" or "dimensions" or "stats" or "advancements") return false;
        if (parts[0] is "region" or "entities" or "poi" && parts.Length == 2)
        {
            var n = parts[1].Split('.');
            return n.Length == 4 && int.TryParse(n[1], out var x) && int.TryParse(n[2], out var z) && Math.Abs(x - rx) <= 1 && Math.Abs(z - rz) <= 1;
        }
        return parts[0] is "data" or "playerdata" or "datapacks";
    }

    internal static (int, int) PlayerRegion(string levelDat)
    {
        try
        {
            var data = Nbt.ReadGzip(levelDat)?.GetValueOrDefault("Data") as Dictionary<string, object?>;
            if ((data?.GetValueOrDefault("Player") as Dictionary<string, object?>)?.GetValueOrDefault("Pos") is List<object?> { Count: 3 } pos)
                return ((int)Math.Floor((double)pos[0]! / 512), (int)Math.Floor((double)pos[2]! / 512));
            if (data?.GetValueOrDefault("SpawnX") is int sx && data.GetValueOrDefault("SpawnZ") is int sz)
                return ((int)Math.Floor(sx / 512.0), (int)Math.Floor(sz / 512.0));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or InvalidCastException) { }
        return (0, 0);
    }

    // ---------- Где игра хранит FPS ----------

    /// <summary>
    /// Поле Minecraft.fps под именами, с которыми работает игра: без обфускации (26.x) — как есть, ванилла —
    /// по официальным маппингам Mojang, Fabric/Quilt — дальше через intermediary, NeoForge 1.20.5+ — имена Mojang.
    /// </summary>
    public async Task<(string Class, string Field)> ResolveFpsFieldAsync(Instance inst, CancellationToken ct)
    {
        const string named = "net.minecraft.client.Minecraft";
        var jar = LauncherPaths.VersionJar(inst.GameVersion);
        using (var zip = ZipFile.OpenRead(jar))
            if (zip.GetEntry("net/minecraft/client/Minecraft.class") is not null) return (named, "fps");
        if (inst.Loader == LoaderType.NeoForge && !IsBefore(inst.GameVersion, 1, 20, 5)) return (named, "fps");
        if (inst.Loader == LoaderType.Forge || inst.Loader == LoaderType.NeoForge)
            throw new FpsBenchmarkException(L.T("Замер пока не работает с Forge и старым NeoForge — создайте копию версии на Fabric или ванилле."));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(LauncherPaths.VersionJson(inst.GameVersion), ct));
        var url = json?["downloads"]?["client_mappings"]?["url"]?.GetValue<string>()
                  ?? throw new FpsBenchmarkException(L.T("Для этой версии Mojang не публикует маппинги — замер недоступен."));
        var (obfClass, obfField) = ParseMojangMappings(await CachedTextAsync(url, $"client-{inst.GameVersion}.txt", ct), named, "fps")
                                   ?? throw new FpsBenchmarkException(L.T("Не нашли счётчик FPS в этой версии игры."));
        if (inst.Loader is LoaderType.Vanilla) return (obfClass, obfField);

        var tinyUrl = $"https://maven.fabricmc.net/net/fabricmc/intermediary/{inst.GameVersion}/intermediary-{inst.GameVersion}-v2.jar";
        var tinyJar = await CachedBytesAsync(tinyUrl, $"intermediary-{inst.GameVersion}-v2.jar", ct);
        using var tz = new ZipArchive(new MemoryStream(tinyJar));
        using var reader = new StreamReader(tz.GetEntry("mappings/mappings.tiny")!.Open());
        return ParseTiny(await reader.ReadToEndAsync(ct), obfClass, obfField)
               ?? throw new FpsBenchmarkException(L.T("Не нашли счётчик FPS в маппингах Fabric."));
    }

    internal static bool IsBefore(string version, int major, int minor, int patch)
    {
        var p = version.Split('.', '-').Select(s => int.TryParse(s, out var n) ? n : 0).Concat(new[] { 0, 0, 0 }).ToArray();
        return (p[0], p[1], p[2]).CompareTo((major, minor, patch)) < 0;
    }

    /// <summary>ProGuard-маппинги Mojang: «a.b.Minecraft -> fgo:», ниже поля «    int fps -> aw».</summary>
    internal static (string, string)? ParseMojangMappings(string text, string cls, string field)
    {
        string? obf = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('#')) continue; // # {"fileName":…} — комментарии ProGuard
            if (!line.StartsWith(' '))
            {
                if (obf is not null) return null;
                if (line.StartsWith(cls + " -> ", StringComparison.Ordinal)) obf = line[(cls.Length + 4)..].TrimEnd('\r', ':');
                continue;
            }
            if (obf is null) continue;
            var t = line.Trim();
            if (!t.Contains('(') && t.StartsWith("int " + field + " -> ", StringComparison.Ordinal)) return (obf, t[(field.Length + 8)..]);
        }
        return null;
    }

    /// <summary>Tiny v2: «c\tobf\tnet/minecraft/class_310», поле «\tf\tI\tobf\tfield_1738».</summary>
    internal static (string, string)? ParseTiny(string text, string obfClass, string obfField)
    {
        string? cls = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var cols = line.Split('\t');
            if (cols.Length >= 3 && cols[0] == "c")
            {
                if (cls is not null) return null;
                if (cols[1] == obfClass) cls = cols[2].Replace('/', '.');
            }
            else if (cls is not null && cols.Length >= 5 && cols[0] == "" && cols[1] == "f" && cols[2] == "I" && cols[3] == obfField)
                return (cls, cols[4]);
        }
        return null;
    }

    private async Task<string> CachedTextAsync(string url, string name, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await CachedBytesAsync(url, name, ct));

    private async Task<byte[]> CachedBytesAsync(string url, string name, CancellationToken ct)
    {
        var path = Path.Combine(LauncherPaths.Cache, "mappings", name);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
        var bytes = await httpFactory.CreateClient(DownloadService.HttpClientName).GetByteArrayAsync(url, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return bytes;
    }
}
