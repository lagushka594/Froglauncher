using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.Services;

public sealed record CrashInfo(int ExitCode, IReadOnlyList<string> LastLines, string? CrashReportPath)
{
    public string Text => string.Join(Environment.NewLine, LastLines);
}

/// <summary>Запущенная игра.</summary>
public sealed class GameSession(Process process, Instance instance, DateTimeOffset startedAt)
{
    private readonly ConcurrentQueue<string> _log = new();
    public Process Process { get; } = process;
    public Instance Instance { get; } = instance;
    public DateTimeOffset StartedAt { get; } = startedAt;

    internal void Append(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > 2000) _log.TryDequeue(out _);
    }

    public IReadOnlyList<string> Tail(int n) => _log.TakeLast(n).ToList();
}

/// <summary>
/// Запуск Minecraft. ВАЖНО: здесь нет и не должно быть проверок подписки —
/// игра запускается всегда, на любом тире, даже без входа в аккаунт FrogLauncher.
/// </summary>
public sealed class LaunchService(
    VersionService versions,
    ModLoaderService loaders,
    JavaService java,
    GameAuthService auth,
    IHttpClientFactory httpFactory,
    ILogger<LaunchService> log)
{
    public const string LauncherName = "FrogLauncher";
    public static readonly string LauncherVersion =
        typeof(LaunchService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public GameSession? Current { get; private set; }
    public event Action<string>? OutputReceived;
    public event Action<GameSession, CrashInfo?>? Exited;

    /// <summary>Установить недостающее и запустить игру.</summary>
    /// <param name="server">Адрес сервера для автоподключения (host или host:port) или null.</param>
    public async Task<GameSession> LaunchAsync(Instance inst, GameAccount account, bool autotune,
        IProgress<DownloadProgress>? progress, CancellationToken ct, string? server = null)
    {
        if (Current is { Process.HasExited: false })
            throw new InvalidOperationException(L.T("Игра уже запущена"));

        // 1. Версия + модлоадер (повторная установка быстрая: всё проверяется по хэшам).
        var versionId = await loaders.InstallAsync(inst.Loader, inst.GameVersion, inst.LoaderVersion, progress, ct);
        inst.LaunchVersionId = versionId;
        var v = await versions.ResolveAsync(versionId, ct);

        // 2. Java: ручной выбор из инстанса либо автоподбор.
        string javaExe;
        var javaMajor = JavaService.RequiredMajor(v);
        if (!string.IsNullOrEmpty(inst.JavaPath) && File.Exists(inst.JavaPath)) javaExe = inst.JavaPath;
        else javaExe = (await java.EnsureAsync(javaMajor, progress, ct)).JavaExe;

        // 3. Аккаунт: обновить токен Microsoft при необходимости.
        progress?.Report(DownloadProgress.Indeterminate(L.T("Проверяем аккаунт")));
        account = await auth.EnsureFreshAsync(account, ct);

        // 4. Natives и командная строка.
        progress?.Report(DownloadProgress.Indeterminate(L.T("Запускаем игру")));
        Directory.CreateDirectory(inst.GameDirectory);
        var nativesDir = Path.Combine(inst.Directory, "natives");
        ExtractNatives(v, nativesDir);
        EnsureVersionJar(v);

        var args = BuildArguments(v, inst, account, nativesDir, autotune, javaMajor, server);

        // Ноутбуки с двумя видеокартами: просим Windows отдать игре дискретную.
        if (inst.PreferDiscreteGpu)
        {
            try { GpuPreference.Apply(javaExe, discrete: true); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.LogWarning(ex, "Не удалось задать предпочтение видеокарты");
            }
        }
        var psi = new ProcessStartInfo(javaExe)
        {
            WorkingDirectory = inst.GameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        log.LogInformation("Запуск {Instance} ({Version}) ник {Nick}, Java {Java}", inst.Name, versionId, account.Name, javaExe);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new GameSession(process, inst, DateTimeOffset.Now);

        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (CleanLogLine(e.Data) is not { } raw) return;
            // Токен доступа в лог не пишем, даже если игра его напечатала.
            var line = account.AccessToken.Length > 8 ? raw.Replace(account.AccessToken, "<token>") : raw;
            session.Append(line);
            OutputReceived?.Invoke(line);
        }

        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) => OnExited(session);

        _stopRequested = false;
        if (!process.Start()) throw new InvalidOperationException(L.T("Не удалось запустить Java"));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.PriorityClass = inst.Priority switch
            {
                "high" => ProcessPriorityClass.High,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogWarning(ex, "Не удалось изменить приоритет процесса");
        }
        Current = session;
        inst.LastPlayedAt = DateTimeOffset.Now;
        return session;
    }

    private volatile bool _stopRequested;

    public void Stop()
    {
        if (Current is { Process.HasExited: false } s)
        {
            _stopRequested = true;
            log.LogInformation("Остановка игры по кнопке «Стоп»");
            s.Process.Kill(entireProcessTree: true);
        }
    }

    private void OnExited(GameSession s)
    {
        var code = s.Process.ExitCode;
        CrashInfo? crash = null;
        // 0 — нормальный выход; остановка кнопкой «Стоп» — тоже не крэш.
        if (code != 0 && !_stopRequested)
        {
            var reports = Path.Combine(s.Instance.GameDirectory, "crash-reports");
            var report = Directory.Exists(reports)
                ? new DirectoryInfo(reports).GetFiles("*.txt")
                    .Where(f => f.LastWriteTime >= s.StartedAt.LocalDateTime)
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName
                : null;
            crash = new CrashInfo(code, s.Tail(50), report);
            log.LogWarning("Игра завершилась с кодом {Code}", code);
        }

        Exited?.Invoke(s, crash);
    }

    /// <summary>Загрузка лога на mclo.gs (только по кнопке пользователя). Возвращает ссылку.</summary>
    public async Task<string> UploadToMclogsAsync(CrashInfo crash, CancellationToken ct)
    {
        var text = crash.CrashReportPath is { } p && File.Exists(p)
            ? await File.ReadAllTextAsync(p, ct) + L.T("\n\n----- последние строки лога -----\n") + crash.Text
            : crash.Text;

        var http = httpFactory.CreateClient(DownloadService.HttpClientName);
        using var resp = await http.PostAsync("https://api.mclo.gs/1/log",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("content", text) }), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return json?["success"]?.GetValue<bool>() == true
            ? json["url"]!.GetValue<string>()
            : throw new InvalidOperationException(json?["error"]?.GetValue<string>() ?? L.T("mclo.gs не принял лог"));
    }

    /// <summary>
    /// Игра с конфигом логирования Mojang пишет log4j-XML. Оставляем только текст сообщений
    /// (CDATA) и стектрейсы, служебные теги выбрасываем. null — строку не показывать.
    /// </summary>
    internal static string? CleanLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var start = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start >= 0)
        {
            start += "<![CDATA[".Length;
            var end = line.IndexOf("]]>", start, StringComparison.Ordinal);
            var text = (end >= 0 ? line[start..end] : line[start..]).TrimEnd();
            return text.Length == 0 ? null : text;
        }

        var t = line.TrimStart();
        if (t.StartsWith("<log4j:", StringComparison.Ordinal) || t.StartsWith("</log4j:", StringComparison.Ordinal) || t == "]]>")
            return null;
        return line.TrimEnd();
    }

    // ---------------- Командная строка ----------------

    internal static List<string> BuildArguments(VersionInfo v, Instance inst, GameAccount account, string nativesDir, bool autotune,
        int javaMajor = 17, string? server = null)
    {
        // 1.20+: Quick Play (--quickPlayMultiplayer); старые версии понимают --server/--port.
        var quickPlay = server is not null && (v.Arguments?.Game.Any(a => a.ToString().Contains("quickPlayMultiplayer")) ?? false);
        var features = new LaunchFeatures(inst.Demo, inst.Width is > 0 && inst.Height is > 0, quickPlay);
        var classpath = BuildClasspath(v);
        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = v.Id,
            ["game_directory"] = inst.GameDirectory,
            ["assets_root"] = LauncherPaths.Assets,
            ["game_assets"] = VersionService.VirtualAssetsDir(v) ?? LauncherPaths.Assets,
            ["assets_index_name"] = v.AssetIndex?.Id ?? v.Assets ?? "legacy",
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = account.AccessToken,
            ["auth_xuid"] = account.Xuid ?? "0",
            ["clientid"] = "0",
            ["user_type"] = account.Type == AccountType.Microsoft ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = LauncherName,
            ["resolution_width"] = (inst.Width ?? 854).ToString(),
            ["resolution_height"] = (inst.Height ?? 480).ToString(),
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = string.Join(Path.PathSeparator, classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = LauncherPaths.Libraries,
            ["quickPlayMultiplayer"] = server ?? "",
        };

        string Sub(string s)
        {
            foreach (var (k, val) in vars) s = s.Replace("${" + k + "}", val);
            return s;
        }

        var args = new List<string>();

        // --- JVM ---
        var ram = Math.Max(512, inst.RamMb);
        args.Add($"-Xmx{ram}M");
        args.Add($"-Xms{Math.Clamp(inst.RamMinMb ?? Math.Min(ram, 1024), 256, ram)}M");

        // Сборщик мусора. ZGC есть только в Java 17+ — на старой Java остаёмся на G1.
        var gc = inst.Gc == "zgc" && javaMajor < 17 ? "g1" : inst.Gc;
        if (gc == "zgc")
        {
            args.Add("-XX:+UseZGC");
            if (javaMajor >= 21) args.Add("-XX:+ZGenerational");
        }
        else if (gc == "parallel") args.Add("-XX:+UseParallelGC");
        else if (!(autotune && inst.JvmAutotune)) args.Add("-XX:+UseG1GC");

        if (gc == "g1" && autotune && inst.JvmAutotune)
            args.AddRange(new[]
            {
                // Frog+ jvm_autotune: G1 с короткими паузами (флаги в духе Aikar), подходят для 1.8+.
                "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200",
                "-XX:+UnlockExperimentalVMOptions", "-XX:+DisableExplicitGC", "-XX:G1NewSizePercent=30",
                "-XX:G1MaxNewSizePercent=40", "-XX:G1HeapRegionSize=8M", "-XX:G1ReservePercent=20",
                "-XX:InitiatingHeapOccupancyPercent=15", "-XX:+AlwaysPreTouch",
            });

        if (v.Logging?.Client is { } logging && File.Exists(VersionService.LoggingConfigPath(logging.File)))
            args.Add(logging.Argument.Replace("${path}", VersionService.LoggingConfigPath(logging.File)));

        if (!string.IsNullOrWhiteSpace(inst.JvmArgs))
            args.AddRange(SplitArgs(inst.JvmArgs));

        if (v.Arguments?.Jvm is { Count: > 0 } jvm)
            args.AddRange(ExpandArgs(jvm, features).Select(Sub));
        else
        {
            // До 1.13 JVM-аргументов в json нет — стандартный набор официального лаунчера.
            args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
            args.Add(Sub("-Djava.library.path=${natives_directory}"));
            args.Add(Sub("-Dminecraft.launcher.brand=${launcher_name}"));
            args.Add(Sub("-Dminecraft.launcher.version=${launcher_version}"));
            args.Add("-cp");
            args.Add(vars["classpath"]);
        }

        args.Add(v.MainClass ?? throw new InvalidDataException(L.T("В version.json нет mainClass")));

        // --- Игра ---
        if (v.Arguments?.Game is { Count: > 0 } game)
            args.AddRange(ExpandArgs(game, features).Select(Sub));
        else if (v.MinecraftArguments is { } legacy)
        {
            args.AddRange(legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Sub));
            if (inst.Demo) args.Add("--demo");
            if (features.HasCustomResolution)
                args.AddRange(new[] { "--width", vars["resolution_width"], "--height", vars["resolution_height"] });
        }

        if (inst.Fullscreen) args.Add("--fullscreen");
        if (server is not null && !quickPlay)
        {
            var (host, port) = ServerPing.ParseAddress(server);
            args.AddRange(new[] { "--server", host, "--port", port.ToString() });
        }
        return args;
    }

    /// <summary>Аргументы нового формата: строки и { rules, value } с учётом features.</summary>
    internal static IEnumerable<string> ExpandArgs(List<JsonElement> list, LaunchFeatures features)
    {
        foreach (var el in list)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                yield return el.GetString()!;
                continue;
            }

            var rules = el.TryGetProperty("rules", out var r) ? r.Deserialize<List<Rule>>(Json.Mojang) : null;
            if (!Rules.Allows(rules, features)) continue;
            if (!el.TryGetProperty("value", out var value)) continue;

            if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
            else foreach (var s in value.EnumerateArray()) yield return s.GetString()!;
        }
    }

    internal static List<string> BuildClasspath(VersionInfo v)
    {
        var cp = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in v.Libraries.Where(l => Rules.Allows(l.Rules) && l.Clientreq != false))
        {
            // Чисто нативные библиотеки старого формата в classpath не нужны.
            if (lib.Natives is not null && lib.Downloads?.Artifact is null) continue;
            var path = Path.Combine(LauncherPaths.Libraries, lib.Downloads?.Artifact?.Path ?? Maven.ToPath(lib.Name));
            if (seen.Add(path)) cp.Add(path);
        }

        cp.Add(LauncherPaths.VersionJar(v.Id));
        return cp;
    }

    /// <summary>
    /// Как официальный лаунчер: у версии-наследника (Fabric/Forge) свой versions/{id}/{id}.jar — копия
    /// родительского client.jar. Forge 1.17+ исключает из модулей именно ${version_name}.jar;
    /// с ванильным именем jar оба модуля экспортируют net.minecraft.* и игра падает.
    /// </summary>
    internal static void EnsureVersionJar(VersionInfo v)
    {
        var own = LauncherPaths.VersionJar(v.Id);
        var parent = LauncherPaths.VersionJar(v.Jar ?? v.Id);
        if (own == parent || !File.Exists(parent)) return;
        if (File.Exists(own) && new FileInfo(own).Length == new FileInfo(parent).Length) return;
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.Copy(parent, own, overwrite: true);
    }

    /// <summary>Распаковка .dll из natives-библиотек (старый формат classifiers и новый natives-windows).</summary>
    internal static void ExtractNatives(VersionInfo v, string nativesDir)
    {
        if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, recursive: true);
        Directory.CreateDirectory(nativesDir);

        foreach (var lib in v.Libraries.Where(l => Rules.Allows(l.Rules)))
        {
            string? jar = null;
            if (VersionService.NativeClassifier(lib) is { } cls && lib.Downloads?.Classifiers?.GetValueOrDefault(cls) is { Path: { } p })
                jar = Path.Combine(LauncherPaths.Libraries, p);
            else if (lib.Name.Split(':') is { Length: > 3 } parts && parts[3].StartsWith("natives-windows", StringComparison.Ordinal))
            {
                // Новый формат (1.19+): отдельные артефакты natives-windows[-x86|-arm64] — берём свою архитектуру.
                var suffix = parts[3]["natives-windows".Length..];
                var mine = Rules.IsArm64 ? "-arm64" : Rules.IsX86 ? "-x86" : "";
                if (suffix != mine) continue;
                jar = Path.Combine(LauncherPaths.Libraries, lib.Downloads?.Artifact?.Path ?? Maven.ToPath(lib.Name));
            }

            if (jar is null || !File.Exists(jar)) continue;
            var exclude = lib.Extract?.Exclude ?? new List<string> { "META-INF/" };
            using var zip = ZipFile.OpenRead(jar);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || exclude.Any(x => e.FullName.StartsWith(x, StringComparison.Ordinal))) continue;
                if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                e.ExtractToFile(Path.Combine(nativesDir, e.Name), overwrite: true);
            }
        }
    }

    /// <summary>Разбор пользовательских JVM-аргументов с учётом кавычек.</summary>
    internal static IEnumerable<string> SplitArgs(string s)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
