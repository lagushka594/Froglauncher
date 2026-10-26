// Использование: LaunchSmoke <версия> <vanilla|fabric|quilt|forge|neoforge> [секунд_игры]
// Ставит всё нужное, запускает игру оффлайн-ником FrogSmoke, ждёт загрузки и закрывает её.
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var version = args.ElementAtOrDefault(0) ?? "1.20.1";
var loader = Enum.Parse<LoaderType>(args.ElementAtOrDefault(1) ?? "fabric", ignoreCase: true);
var seconds = int.Parse(args.ElementAtOrDefault(2) ?? "60");

LauncherPaths.EnsureCreated();
var services = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
    .AddHttpClient()
    .AddSingleton(TimeProvider.System);
services.AddHttpClient(DownloadService.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("FrogLauncher-Smoke/1.0"));
services.Configure<LauncherOptions>(_ => { });
foreach (var t in new[] { typeof(DownloadService), typeof(VersionService), typeof(JavaService), typeof(ModLoaderService),
             typeof(GameAuthService), typeof(LaunchService) })
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
var account = new GameAccount { Type = AccountType.Offline, Name = "FrogSmoke", Uuid = GameAuthService.OfflineUuid("FrogSmoke") };

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
await launch.LaunchAsync(inst, account, autotune: false, progress, CancellationToken.None);
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
await Task.Delay(3000);
launch.Stop();
await Task.WhenAny(exited.Task, Task.Delay(5000));
Console.WriteLine("[ok] остановлено кнопкой «Стоп»");
return first == ready.Task ? 0 : 2;
