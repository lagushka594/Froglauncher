using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Frog.Shared;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.ViewModels;

/// <summary>Главная: выбор инстанса, «Играть»/«Стоп», прогресс, лог и разбор крэша.</summary>
public partial class HomeViewModel : ObservableObject, IPageActivated
{
    private readonly InstanceService _instances;
    private readonly LaunchService _launcher;
    private readonly GameAuthService _accounts;
    private readonly EntitlementService _entitlements;
    private readonly DownloadService _downloads;
    private readonly SettingsService _settings;
    private readonly DiscordPresenceService _presence;
    private readonly ToastService _toasts;
    private readonly ILogger<HomeViewModel> _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<Instance> Instances { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ProgressState Progress { get; } = new();

    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool _isRunning;
    [ObservableProperty] private string _lastLogLine = "";

    public bool ShowStop => IsBusy || IsRunning;
    [ObservableProperty] private CrashInfo? _crash;
    [ObservableProperty] private string? _mclogsUrl;
    [ObservableProperty] private bool _showAdBanner;
    [ObservableProperty] private string _mascot = "/Assets/ui/frog_idle.png";
    [ObservableProperty] private string _accountLabel = "Аккаунт не выбран";

    public HomeViewModel(InstanceService instances, LaunchService launcher, GameAuthService accounts,
        EntitlementService entitlements, DownloadService downloads, SettingsService settings,
        DiscordPresenceService presence, ToastService toasts, ILogger<HomeViewModel> log)
    {
        _instances = instances;
        _launcher = launcher;
        _accounts = accounts;
        _entitlements = entitlements;
        _downloads = downloads;
        _settings = settings;
        _presence = presence;
        _toasts = toasts;
        _log = log;

        launcher.OutputReceived += line => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line);
            LastLogLine = line;
            while (LogLines.Count > 300) LogLines.RemoveAt(0);
        });
        launcher.Exited += (s, crash) => Application.Current.Dispatcher.BeginInvoke(() => OnGameExited(s, crash));
        entitlements.Changed += () => Application.Current.Dispatcher.BeginInvoke(UpdateTierUi);
        accounts.Changed += () => Application.Current.Dispatcher.BeginInvoke(UpdateAccount);
    }

    public Task LoadAsync()
    {
        Instances.ReplaceWith(_instances.List());
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == _settings.Current.SelectedInstanceId) ?? Instances.FirstOrDefault();
        UpdateTierUi();
        UpdateAccount();
        return Task.CompletedTask;
    }

    public Task OnActivatedAsync() => IsBusy || IsRunning ? Task.CompletedTask : LoadAsync();

    partial void OnSelectedInstanceChanged(Instance? value)
    {
        if (value is null) return;
        _settings.Current.SelectedInstanceId = value.Id;
        _settings.Save();
    }

    private bool CanPlay => !IsBusy && !IsRunning;

    /// <summary>Запуск игры. Подписка здесь НЕ проверяется — играть можно всегда.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (SelectedInstance is not { } inst)
        {
            _toasts.Show("Добавьте версию в «Каталоге»", ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Catalog));
            return;
        }

        if (_accounts.Selected is not { } account)
        {
            _toasts.Show("Добавьте аккаунт в «Профиле» — подойдёт и оффлайн-ник", ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Profile));
            return;
        }

        IsBusy = true;
        Crash = null;
        MclogsUrl = null;
        LogLines.Clear();
        Mascot = "/Assets/ui/loading_think.png";
        _cts = new CancellationTokenSource();
        _downloads.Parallelism = _entitlements.IsAllowed(EntitlementKeys.PriorityDownload) ? 16 : 8;

        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Apply(p);
            _ = _presence.SetAsync(new PresenceState($"Готовит {inst.DisplayVersion}",
                p.TotalBytes > 0 ? $"{p.Stage}: {p.Percent:0}%" : p.Stage, SmallImage: "download", SmallText: "Загрузка"));
        });

        try
        {
            var session = await _launcher.LaunchAsync(inst, account,
                _entitlements.IsAllowed(EntitlementKeys.JvmAutotune), progress, _cts.Token);
            _instances.Save(inst);
            IsRunning = true;
            Mascot = "/Assets/ui/frog_cool.png";
            await _presence.SetAsync(new PresenceState(
                $"Играет в Minecraft {inst.GameVersion}",
                inst.Loader == LoaderType.Vanilla ? inst.Name : $"{inst.Name} · {inst.Loader}",
                session.StartedAt, SmallImage: "playing", SmallText: account.Name));

            if (_settings.Current.CloseOnLaunch) Application.Current.MainWindow!.WindowState = WindowState.Minimized;
        }
        catch (OperationCanceledException)
        {
            _toasts.Show("Запуск отменён");
            Mascot = "/Assets/ui/frog_idle.png";
            await _presence.SetIdleAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка запуска");
            _toasts.Show(ex.Message, ToastKind.Error, 7);
            Mascot = "/Assets/ui/frog_sad.png";
            await _presence.SetIdleAsync();
        }
        finally
        {
            IsBusy = false;
            Progress.Reset();
        }
    }

    private bool CanStop => IsBusy || IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (IsBusy) _cts?.Cancel();
        else _launcher.Stop();
    }

    [RelayCommand]
    private async Task UploadCrashAsync()
    {
        if (Crash is null) return;
        try
        {
            MclogsUrl = await _launcher.UploadToMclogsAsync(Crash, CancellationToken.None);
            Clipboard.SetText(MclogsUrl);
            _toasts.Show("Ссылка на лог скопирована 🐸", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _toasts.Show("mclo.gs: " + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenMclogs()
    {
        if (MclogsUrl is not null) Shell.Open(MclogsUrl);
    }

    [RelayCommand]
    private void CopyCrash()
    {
        if (Crash is not null) Clipboard.SetText(Crash.Text);
    }

    [RelayCommand]
    private static void OpenSubscription() => WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));

    private async void OnGameExited(GameSession s, CrashInfo? crash)
    {
        IsRunning = false;
        Crash = crash;
        Mascot = crash is null ? "/Assets/ui/frog_happy.png" : "/Assets/ui/frog_sad.png";
        if (crash is not null) _toasts.Show($"Игра упала (код {crash.ExitCode}). Последние строки лога — на главной.", ToastKind.Error, 7);
        if (Application.Current.MainWindow is { WindowState: WindowState.Minimized } w) w.WindowState = WindowState.Normal;
        await _presence.SetIdleAsync();
    }

    private void UpdateTierUi() =>
        ShowAdBanner = !_entitlements.IsAllowed(EntitlementKeys.NoAds);

    private void UpdateAccount() =>
        AccountLabel = _accounts.Selected is { } a ? $"{a.Name} · {a.TypeLabel}" : "Аккаунт не выбран";
}
