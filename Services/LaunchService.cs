using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

public sealed class LaunchResult
{
    public int ExitCode { get; set; }
    public bool Crashed { get; set; }
    public bool StoppedByUser { get; set; }
    public string LastLines { get; set; } = "";
    public string? MclogsUrl { get; set; }
}

/// <summary>Формирование командной строки (classpath, natives, JVM/game-аргументы), запуск, краш-анализ.</summary>
public sealed class LaunchService
{
    private Process? _proc;
    private bool _userStop;

    public bool IsRunning => _proc is { HasExited: false };

    public void Stop()
    {
        try { _userStop = true; _proc?.Kill(true); }
        catch (Exception ex) { Log.Warn("Stop: " + ex.Message); }
    }

    public async Task<LaunchResult> RunAsync(Instance inst, Account acc, JavaInfo java, Action<string> onLine, Action<int>? onStarted = null)
    {
        var (args, _) = await Task.Run(() => BuildArguments(inst, acc));
        Directory.CreateDirectory(inst.GameDir);

        var psi = new ProcessStartInfo(java.Exe)
        {
            WorkingDirectory = inst.GameDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // токен в лог не пишем
        Log.Info($"Запуск: {java.Exe} {string.Join(' ', args.Select(a => a == acc.AccessToken ? "***" : a))}");

        var tail = new Queue<string>();
        void Push(string? line)
        {
            if (line == null) return;
            lock (tail) { tail.Enqueue(line); while (tail.Count > 400) tail.Dequeue(); }
            onLine(line);
        }

        _userStop = false;
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => Push(e.Data);
        _proc.ErrorDataReceived += (_, e) => Push(e.Data);
        _proc.Start();
        onStarted?.Invoke(_proc.Id);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        await _proc.WaitForExitAsync();
        await Task.Delay(300); // дочитать буфер вывода

        var res = new LaunchResult { ExitCode = _proc.ExitCode, StoppedByUser = _userStop };
        string[] lines; lock (tail) lines = tail.ToArray();
        bool marker = lines.Any(l => l.Contains("---- Minecraft Crash Report ----"));
        res.Crashed = !_userStop && (res.ExitCode != 0 || marker);
        if (res.Crashed)
        {
            res.LastLines = string.Join(Environment.NewLine, lines.TakeLast(50));
            res.MclogsUrl = await UploadToMclogsAsync(string.Join("\n", lines));
        }
        _proc.Dispose(); _proc = null;
        return res;
    }

    private static async Task<string?> UploadToMclogsAsync(string content)
    {
        try
        {
            using var resp = await Http.Client.PostAsync(ApiConfig.MclogsUpload,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["content"] = content }));
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            return (bool?)json?["success"] == true ? (string?)json["url"] : null;
        }
        catch (Exception ex) { Log.Warn("mclo.gs: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------

    public (List<string> args, string mainClass) BuildArguments(Instance inst, Account acc)
    {
        var v = VersionJson.LoadMerged(inst.LaunchVersionId);
        var libs = Libraries.Resolve(v);
        var jarId = (string)v["_jarId"]!;

        // natives
        var nativesDir = Path.Combine(inst.Dir, "natives");
        ExtractNatives(libs, nativesDir);

        // classpath
        var cp = new List<string>();
        foreach (var l in libs.Where(l => l.OnClasspath))
            if (File.Exists(l.AbsPath) && !cp.Contains(l.AbsPath, StringComparer.OrdinalIgnoreCase)) cp.Add(l.AbsPath);
        cp.Add(VersionJson.JarPath(jarId));
        var classpath = string.Join(';', cp);

        string assetIndex = (string?)v["assetIndex"]?["id"] ?? (string?)v["assets"] ?? "legacy";
        string gameAssets = Directory.Exists(VersionService.VirtualAssetsDir(assetIndex))
            ? VersionService.VirtualAssetsDir(assetIndex) : AppPaths.Assets;

        var features = new HashSet<string>();
        if (inst.Demo) features.Add("is_demo_user");
        if (!inst.Fullscreen && inst.Width > 0 && inst.Height > 0) features.Add("has_custom_resolution");
        var (srvHost, srvPort) = ParseServer(inst.ServerAddress);
        if (srvHost != null) features.Add("is_quick_play_multiplayer");

        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = acc.Username, ["version_name"] = inst.LaunchVersionId,
            ["game_directory"] = inst.GameDir, ["assets_root"] = AppPaths.Assets, ["game_assets"] = gameAssets,
            ["assets_index_name"] = assetIndex, ["auth_uuid"] = acc.Uuid, ["auth_access_token"] = acc.AccessToken,
            ["auth_session"] = $"token:{acc.AccessToken}:{acc.Uuid}",
            ["user_type"] = acc.IsMicrosoft ? "msa" : "legacy", ["version_type"] = (string?)v["type"] ?? "release",
            ["user_properties"] = "{}", ["clientid"] = "", ["auth_xuid"] = "",
            ["resolution_width"] = inst.Width.ToString(), ["resolution_height"] = inst.Height.ToString(),
            ["natives_directory"] = nativesDir, ["launcher_name"] = "FrogLauncher",
            ["launcher_version"] = ApiConfig.LauncherVersion, ["classpath"] = classpath,
            ["library_directory"] = AppPaths.Libraries, ["classpath_separator"] = ";",
            ["quickPlayMultiplayer"] = srvHost == null ? "" : $"{srvHost}:{srvPort}"
        };

        var args = new List<string>
        {
            $"-Xms{Math.Max(256, inst.MinRamMb)}M", $"-Xmx{Math.Max(inst.MinRamMb, inst.MaxRamMb)}M",
            "-Dlog4j2.formatMsgNoLookups=true", "-Dfile.encoding=UTF-8"
        };
        args.AddRange(SplitArgs(inst.JvmArgs));

        bool modern = v["arguments"] != null;
        if (modern)
        {
            args.AddRange(Expand(v["arguments"]?["jvm"], vars, features));
            if (!args.Contains(classpath)) { args.Add("-cp"); args.Add(classpath); } // подстраховка
        }
        else
        {
            args.Add("-Djava.library.path=" + nativesDir);
            args.Add("-cp"); args.Add(classpath);
        }

        var mainClass = (string)v["mainClass"]!;
        args.Add(mainClass);

        if (modern) args.AddRange(Expand(v["arguments"]?["game"], vars, features));
        else
        {
            var legacy = (string?)v["minecraftArguments"] ?? "";
            args.AddRange(SplitArgs(legacy).Select(a => Subst(a, vars)));
            if (inst.Demo) args.Add("--demo");
            if (!inst.Fullscreen && !args.Contains("--width")) { args.Add("--width"); args.Add(inst.Width.ToString()); args.Add("--height"); args.Add(inst.Height.ToString()); }
        }
        if (inst.Fullscreen) args.Add("--fullscreen");
        // версии до 1.20 не знают quickPlay — используем классические --server/--port
        if (srvHost != null && !args.Contains("--quickPlayMultiplayer"))
        { args.Add("--server"); args.Add(srvHost); args.Add("--port"); args.Add(srvPort.ToString()); }
        return (args, mainClass);
    }

    private static (string? host, int port) ParseServer(string? addr)
    {
        if (string.IsNullOrWhiteSpace(addr)) return (null, 25565);
        addr = addr.Trim();
        var i = addr.LastIndexOf(':');
        if (i > 0 && int.TryParse(addr[(i + 1)..], out var port)) return (addr[..i], port);
        return (addr, 25565);
    }

    private static void ExtractNatives(List<LibFile> libs, string dir)
    {
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        Directory.CreateDirectory(dir);
        foreach (var l in libs.Where(l => l.Extract && File.Exists(l.AbsPath)))
        {
            try
            {
                using var zip = ZipFile.OpenRead(l.AbsPath);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(e.Name)) continue;
                    if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(dir, e.Name);
                    if (!File.Exists(target)) e.ExtractToFile(target, true);
                }
            }
            catch (Exception ex) { Log.Warn($"Natives {l.Name}: {ex.Message}"); }
        }
    }

    private static List<string> Expand(JsonNode? arr, Dictionary<string, string> vars, ISet<string> features)
    {
        var res = new List<string>();
        if (arr is not JsonArray a) return res;
        foreach (var e in a)
        {
            if (e is JsonValue) { res.Add(Subst((string)e!, vars)); continue; }
            if (e is not JsonObject o || !Rules.Allowed(o["rules"], features)) continue;
            var val = o["value"];
            if (val is JsonArray va) res.AddRange(va.Select(x => Subst((string)x!, vars)));
            else if (val != null) res.Add(Subst((string)val!, vars));
        }
        return res;
    }

    private static string Subst(string s, Dictionary<string, string> vars) =>
        Regex.Replace(s, @"\$\{(\w+)\}", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>Разбор строки аргументов с поддержкой кавычек.</summary>
    public static List<string> SplitArgs(string? s)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return res;
        foreach (Match m in Regex.Matches(s, "\"([^\"]*)\"|(\\S+)"))
            res.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return res;
    }
}
