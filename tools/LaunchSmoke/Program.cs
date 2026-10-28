// Использование: LaunchSmoke <версия> <vanilla|fabric|quilt|forge|neoforge> [секунд_игры]
// Ставит всё нужное, запускает игру оффлайн-ником RibbixSmoke, ждёт загрузки и закрывает её.
using System.IO;
using System.Net.Http;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// LaunchSmoke ping <адрес>... — проверить серверы протоколом Server List Ping.
if (args.ElementAtOrDefault(0) == "ping")
{
    var tasks = args.Skip(1).Select(async a => (a, await ServerPing.PingAsync(a, CancellationToken.None))).ToList();
    foreach (var t in tasks)
    {
        var (addr, st) = await t;
        Console.WriteLine(st.Online
            ? $"OK   {addr,-28} {st.Players,7}/{st.MaxPlayers,-7} {st.PingMs,4} мс  {st.Version}  icon={(st.Icon is null ? "нет" : "есть")}  | {st.Motd.Replace("\n", " / ")}"
            : $"FAIL {addr,-28} {st.Error}");
    }
    return 0;
}

// LaunchSmoke export <папка версии> <файл.mrpack|.zip> — экспорт сборки с настоящим API Modrinth (папка только читается).
if (args.ElementAtOrDefault(0) == "export")
{
    var dir = args[1];
    var src = System.Text.Json.JsonSerializer.Deserialize<Instance>(File.ReadAllText(Path.Combine(dir, "instance.json")), Json.Options)!;
    src.Directory = dir;
    var es = new ServiceCollection().AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true));
    es.AddHttpClient(DownloadService.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("RibbixLauncher-Smoke/1.0"));
    var esp = es.BuildServiceProvider();
    var transfer = new PackTransferService(esp.GetRequiredService<IHttpClientFactory>(), null!, null!,
        esp.GetRequiredService<ILogger<PackTransferService>>());
    var fmt = args[2].EndsWith(".mrpack") ? PackFormat.Mrpack : PackFormat.FullZip;
    var r = await transfer.ExportAsync(src, new ExportOptions(fmt, false, true), args[2], new Progress<string>(Console.WriteLine), CancellationToken.None);
    Console.WriteLine($"OK {r.Path}: ссылками {r.Linked}, внутри {r.Embedded}, offline={r.ModrinthOffline}, {new FileInfo(r.Path).Length / 1024} КБ");
    return 0;
}

// LaunchSmoke adv [ru|en|…] — достижения во всех мирах установленных версий (миры только читаются).
if (args.ElementAtOrDefault(0) == "adv")
{
    var ls = new ServiceCollection().AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true));
    ls.AddSingleton(sp => new InstanceService(null!, sp.GetRequiredService<ILogger<InstanceService>>())).AddSingleton<AdvancementService>(); // List() не трогает тариф
    var adv = ls.BuildServiceProvider().GetRequiredService<AdvancementService>();
    foreach (var w in adv.Worlds())
    {
        var list = adv.Read(w, args.ElementAtOrDefault(1) ?? "ru");
        Console.WriteLine($"{w.Instance.Name} / {w.Name} ({w.Instance.GameVersion}): {list.Count(a => a.IsDone)} из {list.Count}");
        foreach (var a in list.Where(a => a.Done > 0).Take(6)) Console.WriteLine($"  {a.Title} — {a.Description} [{a.Done}/{a.Total}]");
    }
    return 0;
}

// LaunchSmoke fps <версия> <vanilla|fabric> — запустить тестовую версию с агентом FPS и показать строки RIBBIXFPS.
var fpsMode = args.ElementAtOrDefault(0) == "fps";
if (fpsMode) args = args.Skip(1).Append("40").ToArray();

var version = args.ElementAtOrDefault(0) ?? "1.20.1";
var loader = Enum.Parse<LoaderType>(args.ElementAtOrDefault(1) ?? "fabric", ignoreCase: true);
var seconds = int.Parse(args.ElementAtOrDefault(2) ?? "60");

LauncherPaths.EnsureCreated();
var services = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
    .AddHttpClient()
    .AddSingleton(TimeProvider.System);
services.AddHttpClient(DownloadService.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("RibbixLauncher-Smoke/1.0"));
services.Configure<LauncherOptions>(_ => { });
foreach (var t in new[] { typeof(DownloadService), typeof(VersionService), typeof(JavaService), typeof(ModLoaderService),
             typeof(GameAuthService), typeof(LaunchService), typeof(FpsBenchmarkService) })
    services.AddSingleton(t);
var sp = services.BuildServiceProvider();

var launch = sp.GetRequiredService<LaunchService>();
var inst = new Instance
{
    Id = $"smoke-{version}-{loader}".ToLowerInvariant(),
    Name = "Smoke",
    GameVersion = version,
    Loader = loader,
    RamMb = 2048,
    Width = 854,
    Height = 480,
};
inst.Directory = System.IO.Path.Combine(LauncherPaths.Instances, inst.Id);
var account = new GameAccount { Type = AccountType.Offline, Name = "RibbixSmoke", Uuid = GameAuthService.OfflineUuid("RibbixSmoke") };

var lastStage = "";
var progress = new Progress<DownloadProgress>(p =>
{
    if (p.Stage != lastStage) Console.WriteLine($"[stage] {p.Stage}");
    lastStage = p.Stage;
});

var ready = new TaskCompletionSource<string>();
launch.OutputReceived += line =>
{
    if (line.Contains("Setting user") || line.Contains("LWJGL") || line.Contains("OpenAL initialized") || line.Contains("Sound engine started"))
        ready.TrySetResult(line);
};
var exited = new TaskCompletionSource<CrashInfo?>();
launch.Exited += (_, crash) => exited.TrySetResult(crash);

var sw = System.Diagnostics.Stopwatch.StartNew();
LaunchExtras? extras = null;
if (fpsMode)
{
    var (cls, field) = await sp.GetRequiredService<FpsBenchmarkService>().ResolveFpsFieldAsync(inst, CancellationToken.None);
    var agent = System.IO.Path.Combine(AppContext.BaseDirectory, "ribbix-fps-agent.jar");
    using (var res = typeof(FpsBenchmarkService).Assembly.GetManifestResourceStream("RibbixLauncher.ribbix-fps-agent.jar")!)
    using (var fs = File.Create(agent)) res.CopyTo(fs);
    Console.WriteLine($"[fps] поле FPS: {cls}.{field}");
    extras = new LaunchExtras(true, new[] { $"-javaagent:{agent}={cls}:{field}" });
    launch.OutputReceived += line => { if (line.Contains("RIBBIXFPS")) Console.WriteLine("  [fps] " + line); };
}
await launch.LaunchAsync(inst, account, autotune: false, progress, CancellationToken.None, extras: extras);
Console.WriteLine($"[ok] процесс запущен за {sw.Elapsed.TotalSeconds:0} с, ждём загрузки игры…");

var first = await Task.WhenAny(ready.Task, exited.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
if (first == exited.Task)
{
    var crash = await exited.Task;
    Console.WriteLine($"[FAIL] игра завершилась, код {crash?.ExitCode}");
    foreach (var l in crash?.LastLines ?? Array.Empty<string>()) Console.WriteLine("  " + l);
    return 1;
}

Console.WriteLine(first == ready.Task ? $"[PASS] игра загрузилась: {await ready.Task}" : "[WARN] маркер загрузки не встречен, но процесс жив");
foreach (var l in launch.Current!.Tail(8)) Console.WriteLine("  | " + l);
await Task.Delay(fpsMode ? 45000 : 3000);
launch.Stop();
await Task.WhenAny(exited.Task, Task.Delay(5000));
Console.WriteLine("[ok] остановлено кнопкой «Стоп»");
return first == ready.Task ? 0 : 2;
