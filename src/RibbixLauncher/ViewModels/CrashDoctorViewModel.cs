using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

public partial class FixItem(CrashFix fix) : ObservableObject
{
    public CrashFix Fix { get; } = fix;
    [ObservableProperty] private string _label = fix.Label;
    [ObservableProperty] private bool _isDone;
}

public sealed class DiagnosisItem(CrashDiagnosis d)
{
    public string Title { get; } = d.Title;
    public string Explanation { get; } = d.Explanation;
    public ObservableCollection<FixItem> Fixes { get; } = new(d.Fixes.Select(f => new FixItem(f)));
}

/// <summary>
/// «Доктор крашей» на главной: понятные причины вылета и починка в один клик
/// (скачать зависимость, обновить/выключить мод, Java, память, откат модов, поиск виновника).
/// </summary>
public partial class CrashDoctorViewModel(
    ModToolsService tools,
    ModrinthService modrinth,
    JavaService java,
    InstanceService instances,
    ModBisectService bisect,
    GameAuthService accounts,
    ToastService toasts,
    ILogger<CrashDoctorViewModel> log) : ObservableObject
{
    public ObservableCollection<DiagnosisItem> Diagnoses { get; } = new();
    public ProgressState Progress { get; } = new();

    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isBisecting;
    [ObservableProperty] private string _bisectText = "";
    [ObservableProperty] private double _bisectPercent;
    private Instance? _inst;
    private CancellationTokenSource? _bisectCts;
    private static HardwareInfo? _hw;

    /// <summary>Сообщить, что починка поменяла версию (главная перечитает список).</summary>
    public event Action? Fixed;

    public async Task AnalyzeAsync(Instance inst, CrashInfo crash)
    {
        _inst = inst;
        var (text, ctx) = await Task.Run(() =>
        {
            _hw ??= HardwareService.Scan();
            return (CrashDoctor.ReadText(crash),
                new CrashDoctor.Context(inst, tools.List(inst), HardwareService.MaxSafeRamMb(_hw), tools.PendingSnapshot(inst) is not null));
        });
        Show(CrashDoctor.Diagnose(text, ctx));
    }

    private void Show(IEnumerable<CrashDiagnosis> list) => Diagnoses.ReplaceWith(list.Select(d => new DiagnosisItem(d)));

    public void Clear() => Diagnoses.Clear();

    [RelayCommand]
    private async Task FixAsync(FixItem? item)
    {
        if (item is null || _inst is not { } inst || IsWorking || item.IsDone) return;
        if (item.Fix.Kind == FixKind.Bisect) { await BisectAsync(); return; }
        IsWorking = true;
        try
        {
            var done = await ApplyAsync(inst, item.Fix);
            if (done is null) return;
            instances.Save(inst);
            item.IsDone = true;
            item.Label = "✓ " + item.Fix.Label;
            toasts.Show(done, ToastKind.Success, 6);
            Fixed?.Invoke();
        }
        catch (Exception ex) when (ex is ModsException or HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Починка {Kind} {Arg}", item.Fix.Kind, item.Fix.Arg);
            toasts.Show(L.T("Не получилось: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsWorking = false;
            Progress.Reset();
        }
    }

    /// <summary>Выполнить починку. Возвращает текст тоста или null, если делать нечего (тост уже показан).</summary>
    private async Task<string?> ApplyAsync(Instance inst, CrashFix fix)
    {
        var progress = new Progress<DownloadProgress>(Progress.Apply);
        switch (fix.Kind)
        {
            case FixKind.InstallMod:
                var added = await InstallByIdAsync(inst, fix.Arg!);
                return L.T($"Скачано: {string.Join(", ", added.Select(a => a.Title))}. Жмите «Играть» 🐸");
            case FixKind.UpdateMod:
                if (tools.Find(inst, fix.Arg!) is not { } mod) { toasts.Show(L.T("Мод уже не найден в папке mods"), ToastKind.Info); return null; }
                Progress.Apply(DownloadProgress.Indeterminate(L.T("Ищем обновление на Modrinth")));
                var updates = await tools.CheckUpdatesAsync(inst, CancellationToken.None, new[] { mod.FileName });
                if (updates.Count == 0)
                {
                    toasts.Show(L.T($"Новой версии «{mod.Name}» под {inst.GameVersion} на Modrinth нет — выключите мод."), ToastKind.Info, 7);
                    return null;
                }
                await tools.ApplyUpdatesAsync(inst, updates, progress, CancellationToken.None);
                return L.T($"«{mod.Name}» обновлён до {updates[0].NewVersion}. Если снова упадёт — «Вернуть моды как было».");
            case FixKind.DisableMod:
                var file = fix.Arg!.StartsWith("file:") ? fix.Arg[5..] : tools.Find(inst, fix.Arg!)?.FileName;
                if (file is null || !File.Exists(Path.Combine(ModToolsService.ModsDir(inst), file)))
                {
                    toasts.Show(L.T("Мод уже не найден в папке mods"), ToastKind.Info);
                    return null;
                }
                ModToolsService.SetEnabled(inst, file, false);
                return L.T("Мод выключен (не удалён — включить можно в «Каталоге»).");
            case FixKind.Java:
                var major = int.Parse(fix.Arg!);
                var rt = await java.EnsureAsync(major, progress, CancellationToken.None);
                inst.JavaPath = rt.JavaExe;
                return L.T($"Java {major} готова и выбрана для «{inst.Name}».");
            case FixKind.Ram:
                inst.RamMb = int.Parse(fix.Arg!);
                return L.T($"Игре выделено {inst.RamMb / 1024.0:0.#} ГБ.");
            case FixKind.Rollback:
                if (tools.PendingSnapshot(inst) is not { } snap) { toasts.Show(L.T("Снимка модов нет"), ToastKind.Info); return null; }
                tools.Restore(inst, snap);
                return L.T($"Моды возвращены к {snap.CreatedAt.LocalDateTime:dd.MM HH:mm}.");
            case FixKind.Drivers:
                _hw ??= await Task.Run(HardwareService.Scan);
                var gpu = _hw.MainGpu?.Name ?? "";
                Shell.Open(gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "https://www.nvidia.com/Download/index.aspx"
                    : gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "https://www.amd.com/support"
                    : "https://www.intel.com/content/www/us/en/download-center/home.html");
                return L.T("Открыли страницу драйверов. После установки перезагрузите ПК.");
        }
        return null;
    }

    /// <summary>У части модов id в метаданных не совпадает со slug на Modrinth.</summary>
    internal static readonly Dictionary<string, string> SlugAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = "fabric-api", ["fabric-api-base"] = "fabric-api", ["fabric-resource-loader-v0"] = "fabric-api",
        ["cloth_config"] = "cloth-config", ["cloth-config2"] = "cloth-config", ["quilted_fabric_api"] = "qsl",
        ["fabric_language_kotlin"] = "fabric-language-kotlin", ["kotlinforforge"] = "kotlin-for-forge", ["architectury"] = "architectury-api",
    };

    private async Task<IReadOnlyList<InstalledMod>> InstallByIdAsync(Instance inst, string modId)
    {
        var slug = SlugAliases.GetValueOrDefault(modId) ?? modId.Replace('_', '-');
        Progress.Apply(DownloadProgress.Indeterminate(L.T($"Ищем «{modId}» на Modrinth")));
        try
        {
            return await modrinth.InstallAsync(inst, slug, CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var hit = (await modrinth.SearchAsync(inst, modId, 0, CancellationToken.None)).FirstOrDefault()
                      ?? throw new ModsException(L.T($"На Modrinth нет «{modId}» под {inst.GameVersion}"));
            return await modrinth.InstallAsync(inst, hit.ProjectId, CancellationToken.None);
        }
    }

    // ---------- Поиск виновника ----------

    public bool CanBisect => !bisect.IsRunning;

    [RelayCommand]
    private async Task BisectAsync()
    {
        if (_inst is not { } inst || bisect.IsRunning) return;
        if (accounts.Selected is not { } account)
        {
            toasts.Show(L.T("Добавьте аккаунт в «Профиле» — подойдёт и оффлайн-ник"), ToastKind.Info);
            return;
        }
        IsBisecting = true;
        _bisectCts = new CancellationTokenSource();
        var progress = new Progress<BisectStatus>(s =>
        {
            BisectText = L.T($"Шаг {s.Step} из ~{s.EstimatedSteps}: {s.Text}");
            BisectPercent = Math.Min(100, (s.Step - 1) * 100.0 / s.EstimatedSteps);
        });
        try
        {
            var r = await bisect.RunAsync(inst, account, progress, _bisectCts.Token);
            Show(r.Culprit is { } c
                ? new[]
                {
                    new CrashDiagnosis(L.T($"Виновник найден: «{c.Name}»"), r.Message, new[]
                    {
                        new CrashFix(FixKind.UpdateMod, L.T($"Обновить «{c.Name}»"), c.Ids.FirstOrDefault() ?? c.Jar),
                        new CrashFix(FixKind.DisableMod, L.T($"Выключить «{c.Name}»"), "file:" + c.Jar),
                    }),
                }
                : new[] { new CrashDiagnosis(L.T("Виновника не нашли"), r.Message, Array.Empty<CrashFix>()) });
            Fixed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            toasts.Show(L.T("Поиск остановлен, моды возвращены как были"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            log.LogWarning(ex, "Поиск виновника");
            toasts.Show(L.T("Поиск не удался: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsBisecting = false;
            BisectText = "";
        }
    }

    [RelayCommand]
    private void CancelBisect() => _bisectCts?.Cancel();
}
