using System.IO;
using System.Text.Json;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed record BisectStatus(int Step, int EstimatedSteps, int Candidates, string Text);

public sealed record BisectResult(ModInfo? Culprit, int Launches, string Message);

/// <summary>Что было включено до поиска — чтобы вернуть всё, даже если лаунчер закрыли посередине.</summary>
public sealed class BisectState
{
    public List<string> OriginalEnabled { get; set; } = new();
}

/// <summary>
/// Поиск мода-виновника половинным делением: включаем половину подозреваемых (с их зависимостями и
/// библиотеками), запускаем игру и ждём главного меню. Упала — виновник в этой половине, нет — в другой.
/// Из 100 модов — примерно 7 запусков. В конце все моды возвращаются как были.
/// </summary>
public sealed class ModBisectService(LaunchService launcher, ModToolsService tools, ILogger<ModBisectService> log)
{
    /// <summary>Строки лога, после которых игра точно дошла до главного меню.</summary>
    public static readonly string[] ReadyMarkers = { "Sound engine started", "OpenAL initialized" };
    /// <summary>Ошибки загрузчика, после которых игра не запустится (Fabric показывает окно и ждёт).</summary>
    public static readonly string[] FailMarkers = { "Incompatible mods found", "Mod resolution failed", "Incompatible mod set", "Missing or unsupported mandatory dependencies" };

    public static TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public static TimeSpan AfterReady { get; set; } = TimeSpan.FromSeconds(8);

    public bool IsRunning { get; private set; }

    private static string StateFile(Instance inst) => Path.Combine(inst.Directory, "bisect.json");

    /// <summary>Лаунчер закрыли во время поиска — вернуть моды как были.</summary>
    public void RecoverIfInterrupted(Instance inst)
    {
        var file = StateFile(inst);
        if (!File.Exists(file)) return;
        try
        {
            var state = JsonSerializer.Deserialize<BisectState>(File.ReadAllText(file), Json.Options);
            if (state is not null) Apply(inst, state.OriginalEnabled.ToHashSet(StringComparer.OrdinalIgnoreCase), state.OriginalEnabled);
            log.LogInformation("Поиск виновника в {Instance} был прерван — моды возвращены", inst.Id);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { log.LogWarning(ex, "bisect.json"); }
        File.Delete(file);
    }

    public static int EstimateSteps(int candidates) => candidates <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(candidates)) + 1;

    public async Task<BisectResult> RunAsync(Instance inst, GameAccount account, IProgress<BisectStatus>? progress, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException(L.T("Поиск уже идёт"));
        IsRunning = true;
        var all = tools.List(inst).Where(m => m.Enabled).ToList();
        var original = all.Select(m => m.Jar).ToList();
        File.WriteAllText(StateFile(inst), JsonSerializer.Serialize(new BisectState { OriginalEnabled = original }, Json.Options));
        try
        {
            var (culpritJar, launches, reproduced) = await SearchAsync(all, enabled => CrashesAsync(inst, account, enabled, original, ct), progress, ct);
            if (!reproduced)
                return new BisectResult(null, launches, L.T("Со всеми модами игра доходит до главного меню. Значит, она падает позже — в мире или на сервере. Попробуйте «Доктора крашей» после следующего вылета."));
            if (culpritJar is null)
                return new BisectResult(null, launches, L.T("Игра падает даже только с библиотеками (Fabric API и т. п.). Проверьте Java и память в настройках версии."));
            var culprit = all.First(m => m.Jar == culpritJar);
            log.LogInformation("Виновник в {Instance}: {Mod} за {Launches} запусков", inst.Id, culprit.Jar, launches);
            return new BisectResult(culprit, launches, L.T($"Игра падает из-за «{culprit.Name}». Проверено за {launches} {ViewModels.MainViewModel.Plural(launches, "запуск", "запуска", "запусков")}."));
        }
        finally
        {
            Apply(inst, original.ToHashSet(StringComparer.OrdinalIgnoreCase), original);
            try { File.Delete(StateFile(inst)); } catch (IOException) { }
            IsRunning = false;
        }
    }

    /// <summary>
    /// Половинное деление. crashes(включённые jar) — запуск игры: true, если упала. Первый запуск — со всеми модами:
    /// не упала — Reproduced = false. Только библиотеки — null.
    /// </summary>
    internal static async Task<(string? Culprit, int Launches, bool Reproduced)> SearchAsync(IReadOnlyList<ModInfo> all, Func<List<string>, Task<bool>> crashes,
        IProgress<BisectStatus>? progress, CancellationToken ct)
    {
        var candidates = all.Where(m => !m.Ids.Any(ModToolsService.CoreLibraries.Contains)).Select(m => m.Jar).ToList();
        var total = EstimateSteps(candidates.Count);
        var step = 1;
        progress?.Report(new BisectStatus(step, total, candidates.Count, L.T("Проверяем, падает ли игра со всеми модами")));
        if (!await crashes(all.Select(m => m.Jar).ToList())) return (null, 1, false);
        while (candidates.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            var half = candidates.Take(candidates.Count / 2).ToList();
            step++;
            progress?.Report(new BisectStatus(step, Math.Max(total, step), candidates.Count,
                L.T($"Проверяем {half.Count} из {candidates.Count} подозреваемых модов")));
            candidates = await crashes(Closure(all, half)) ? half : candidates.Except(half).ToList();
        }
        return (candidates.FirstOrDefault(), step, true);
    }

    /// <summary>Набор для проверки: выбранные моды + библиотеки + всё, от чего они зависят.</summary>
    internal static List<string> Closure(IReadOnlyList<ModInfo> all, IEnumerable<string> pick)
    {
        var byId = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in all) foreach (var id in m.Ids) byId.TryAdd(id, m);
        var set = new HashSet<string>(pick, StringComparer.OrdinalIgnoreCase);
        foreach (var m in all.Where(m => m.Ids.Any(ModToolsService.CoreLibraries.Contains))) set.Add(m.Jar);
        var queue = new Queue<string>(set);
        while (queue.TryDequeue(out var jar))
            foreach (var dep in all.First(m => m.Jar == jar).Depends)
                if (byId.TryGetValue(dep, out var d) && set.Add(d.Jar)) queue.Enqueue(d.Jar);
        return all.Where(m => set.Contains(m.Jar)).Select(m => m.Jar).ToList();
    }

    /// <summary>Включить ровно эти jar из списка участвующих, остальные участвующие — выключить.</summary>
    private void Apply(Instance inst, ISet<string> enable, IEnumerable<string> participants)
    {
        var current = tools.List(inst).ToDictionary(m => m.Jar, m => m.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var jar in participants)
            if (current.TryGetValue(jar, out var file))
                ModToolsService.SetEnabled(inst, file, enable.Contains(jar));
    }

    private async Task<bool> CrashesAsync(Instance inst, GameAccount account, IReadOnlyCollection<string> enabled, IReadOnlyList<string> participants, CancellationToken ct)
    {
        Apply(inst, enabled.ToHashSet(StringComparer.OrdinalIgnoreCase), participants);
        return !await GameStartsAsync(launcher, inst, account, new LaunchExtras(Diagnostic: true), ct);
    }

    /// <summary>Запустить игру и дождаться главного меню. true — дошла и спокойно работала ещё несколько секунд.</summary>
    public static async Task<bool> GameStartsAsync(LaunchService launcher, Instance inst, GameAccount account, LaunchExtras extras, CancellationToken ct,
        Func<string, bool>? ready = null)
    {
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ready ??= line => ReadyMarkers.Any(line.Contains);
        void OnLine(string line)
        {
            if (ready(line)) readyTcs.TrySetResult();
            else if (FailMarkers.Any(line.Contains)) failTcs.TrySetResult();
        }
        void OnExit(GameSession s, CrashInfo? _)
        {
            if (s.Instance.Id == inst.Id) exitTcs.TrySetResult();
        }
        launcher.OutputReceived += OnLine;
        launcher.Exited += OnExit;
        var started = false;
        try
        {
            await launcher.LaunchAsync(inst, account, autotune: false, null, ct, extras: extras);
            started = true;
            var first = await Task.WhenAny(readyTcs.Task, failTcs.Task, exitTcs.Task, Task.Delay(ReadyTimeout, ct));
            ct.ThrowIfCancellationRequested();
            var ok = first == readyTcs.Task && await Task.WhenAny(exitTcs.Task, Task.Delay(AfterReady, ct)) != exitTcs.Task;
            return ok;
        }
        finally
        {
            if (started)
            {
                launcher.Stop();
                await Task.WhenAny(exitTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            }
            launcher.OutputReceived -= OnLine;
            launcher.Exited -= OnExit;
        }
    }
}
