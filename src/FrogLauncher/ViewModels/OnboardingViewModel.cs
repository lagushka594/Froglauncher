using FrogLauncher.Core;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.ViewModels;

/// <summary>
/// Онбординг первого запуска: 0 — приветствие, 1 — ник, 2 — RAM, 3 — установка рекомендуемой версии.
/// Показывается, только если у игрока ещё нет ни аккаунтов, ни версий. «Пропустить» — на любом шаге.
/// </summary>
public partial class OnboardingViewModel(
    SettingsService settings,
    GameAuthService accounts,
    InstanceService instances,
    VersionService versions,
    ModLoaderService loaders,
    ModrinthService modrinth,
    HomeViewModel home,
    ToastService toasts,
    ILogger<OnboardingViewModel> log) : ObservableObject
{
    public HardwareInfo Hardware { get; } = HardwareService.Scan();
    public ProgressState Progress { get; } = new();

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(CanBack), nameof(NextText))]
    private int _step;
    [ObservableProperty] private string _nick = "";
    [ObservableProperty] private string? _nickError;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RamText))] private int _ramMb;
    [ObservableProperty] private bool _optimized = true;
    [ObservableProperty] private string _latestVersion = "…";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _installed;

    public int MaxRamMb => HardwareService.MaxSafeRamMb(Hardware);
    public string RamText => L.T($"{RamMb / 1024.0:0.#} ГБ");
    public string RamHint => L.T($"У вас {Hardware.TotalRamGb:0} ГБ памяти — рекомендуем {HardwareService.RecommendedRamMb(Hardware.TotalRamGb) / 1024} ГБ для Minecraft.");
    public string StepTitle => L.T($"Шаг {Step + 1} из 4");
    public bool CanBack => Step is > 0 and < 3 && !IsInstalling;
    public string NextText => Step switch { 0 => L.T("Начать"), 3 => Installed ? L.T("Играть!") : L.T("Установить"), _ => L.T("Далее") };

    public void ShowIfFirstRun()
    {
        if (settings.Current.OnboardingDone || accounts.Load().Accounts.Count > 0 || instances.List().Count > 0) return;
        Show();
    }

    /// <summary>Показать знакомство заново (кнопка в «Помощи»).</summary>
    public void Show()
    {
        Step = 0;
        Installed = false;
        RamMb = Math.Min(HardwareService.RecommendedRamMb(Hardware.TotalRamGb), MaxRamMb);
        Nick = accounts.Selected?.Name ?? "";
        IsVisible = true;
        _ = LoadLatestAsync();
    }

    private async Task LoadLatestAsync()
    {
        try { LatestVersion = (await versions.GetManifestAsync()).Latest.Release; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { LatestVersion = "1.21.1"; }
    }

    [RelayCommand]
    private void Back()
    {
        if (CanBack) Step--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case 0:
                Step = 1;
                break;
            case 1:
                try
                {
                    if (accounts.Selected?.Name != Nick.Trim()) accounts.AddOffline(Nick);
                    NickError = null;
                    Step = 2;
                }
                catch (GameAuthException ex)
                {
                    NickError = ex.Message;
                }
                break;
            case 2:
                settings.Current.DefaultRamMb = RamMb;
                settings.Save();
                Step = 3;
                OnPropertyChanged(nameof(NextText));
                break;
            case 3 when Installed:
                Finish();
                await home.LoadAsync();
                WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Home));
                if (home.PlayCommand.CanExecute(null)) await home.PlayCommand.ExecuteAsync(null);
                break;
            case 3:
                await InstallAsync();
                break;
        }
    }

    /// <summary>Рекомендуемая версия: последний релиз; «оптимизированная» — с Fabric и Sodium (больше FPS).</summary>
    private async Task InstallAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        OnPropertyChanged(nameof(CanBack));
        try
        {
            var mc = LatestVersion;
            var loader = LoaderType.Vanilla;
            string? loaderVersion = null;
            if (Optimized)
            {
                var fabric = await loaders.GetVersionsAsync(LoaderType.Fabric, mc, CancellationToken.None);
                if ((fabric.FirstOrDefault(v => v.Recommended) ?? fabric.FirstOrDefault()) is { } f)
                {
                    loader = LoaderType.Fabric;
                    loaderVersion = f.Version;
                }
            }

            var inst = instances.Create(loader == LoaderType.Fabric ? L.T($"Minecraft {mc} (оптимизированная)") : $"Minecraft {mc}",
                mc, loader, loaderVersion, RamMb);
            settings.Current.SelectedInstanceId = inst.Id;
            settings.Save();

            var progress = new Progress<DownloadProgress>(Progress.Apply);
            await loaders.InstallAsync(loader, mc, loaderVersion, progress, CancellationToken.None);
            if (loader == LoaderType.Fabric)
            {
                Progress.Apply(DownloadProgress.Indeterminate(L.T("Ставим Sodium — мод на FPS")));
                try
                {
                    await modrinth.InstallAsync(inst, "AANobbMI", CancellationToken.None); // Sodium
                    instances.Save(inst);
                }
                catch (Exception ex) when (ex is ModsException or HttpRequestException)
                {
                    log.LogWarning(ex, "Sodium для {Version} не установился", mc);
                }
            }

            Installed = true;
            OnPropertyChanged(nameof(NextText));
            toasts.Show(L.T($"Версия {inst.DisplayVersion} готова 🐸"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException or TaskCanceledException)
        {
            log.LogWarning(ex, "Онбординг: установка не удалась");
            toasts.Show(L.T("Не удалось скачать игру: ") + ex.Message + L.T(". Можно продолжить и установить позже в «Каталоге»."), ToastKind.Error, 8);
        }
        finally
        {
            IsInstalling = false;
            Progress.Reset();
            OnPropertyChanged(nameof(CanBack));
        }
    }

    [RelayCommand]
    private void Skip() => Finish();

    private void Finish()
    {
        settings.Current.OnboardingDone = true;
        settings.Save();
        IsVisible = false;
    }
}
