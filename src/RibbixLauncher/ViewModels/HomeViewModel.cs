using RibbixLauncher.Core;
﻿using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

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
    private readonly PlayStatsService _stats;
    private readonly TrayService _tray;
    private readonly WorldService _worlds;
    private System.Windows.Threading.DispatcherTimer? _reminder;
    private readonly ILogger<HomeViewModel> _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<Instance> Instances { get; } = new();
    public CrashDoctorViewModel Doctor { get; }
    private readonly ModToolsService _modTools;
    private readonly ModBisectService _bisect;
    private readonly FriendLinkService _friends;
    private readonly SkinService _skins;
    private string? _openedHost;

    [System.Text.RegularExpressions.GeneratedRegex(@"[a-z0-9-]+(\.[a-z0-9-]+)*\.e4mc\.link", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex E4mcRegex();

    /// <summary>
    /// «Открыть мир друзьям»: мод e4mc выдал публичный адрес открытого для сети мира — копируем его, показываем
    /// и включаем «Присоединиться» в Discord, чтобы друзья зашли в один клик.
    /// </summary>
    private void OnWorldOpened(string host)
    {
        if (host == _openedHost || _launcher.Current?.Instance is not { } inst) return;
        _openedHost = host;
        try { Clipboard.SetText(host); } catch (System.Runtime.InteropServices.COMException) { }
        _toasts.Show(L.T($"🌍 Мир открыт для друзей: {host} — адрес скопирован. Друзья: «Сетевая игра» → «Прямое подключение»."), ToastKind.Success, 12);
        _ = _presence.SetAsync(new PresenceState(L.T($"Играет в Minecraft {inst.GameVersion}"), L.T("Открыл мир для друзей 🌍"),
            _launcher.Current.StartedAt, SmallImage: "playing", SmallText: _accounts.Selected?.Name ?? "",
            JoinSecret: FriendLinkService.JoinSecret(inst, host)));
    }
    private readonly PreflightService _preflight;
    private readonly SharedPackService _shared;
    private bool _synced;

    /// <summary>Открыли ссылку ribbix://pack — вступить в общую сборку друга.</summary>
    public async Task JoinSharedPackAsync(string gist)
    {
        if (IsBusy || IsRunning) { _toasts.Show(L.T("Игра уже запущена или загружается"), ToastKind.Info); return; }
        if (MessageBox.Show(L.T("Вступить в общую сборку друга? Лаунчер поставит её и будет сам подтягивать изменения, когда друг меняет моды."),
                "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var inst = await _shared.JoinAsync(gist, _settings.Current.DefaultRamMb, new Progress<DownloadProgress>(Progress.Apply), CancellationToken.None);
            await LoadAsync();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == inst.Id) ?? SelectedInstance;
            _toasts.Show(L.T($"Общая сборка «{inst.Name}» готова 🐸 Изменения друга будут приходить сами"), ToastKind.Success, 7);
        }
        catch (InstanceLimitException ex) { _toasts.Show(ex.Message, ToastKind.Warning, 7); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.IO.IOException or TaskCanceledException)
        {
            _toasts.Show(L.T("Не удалось поставить общую сборку: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsBusy = false;
            Progress.Reset();
        }
    }

    /// <summary>Раз за запуск лаунчера: общие сборки друзей подтягивают изменения.</summary>
    private async Task SyncSharedPacksAsync()
    {
        foreach (var inst in Instances.Where(i => SharedPackService.IsGistId(i.SharedGist)).ToList())
        {
            try
            {
                if (await _shared.SyncAsync(inst, null, CancellationToken.None))
                    _toasts.Show(L.T($"🔄 Общая сборка «{inst.Name}» обновилась — друг поменял моды"), ToastKind.Info, 6);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or System.IO.IOException or TaskCanceledException)
            {
                _log.LogWarning(ex, "Общая сборка {Instance} не обновлена", inst.Id);
            }
        }
    }
    public QuestsViewModel Quests { get; }
    private bool _skipPreflight;

    /// <summary>
    /// Ссылка «Играть с другом»: подтверждение (ссылка пришла извне) → поставить версию с модами → играть
    /// (с автоподключением к серверу, если он есть в ссылке).
    /// </summary>
    public async Task JoinFriendAsync(FriendInvite invite)
    {
        if (IsBusy || IsRunning)
        {
            _toasts.Show(L.T("Игра уже запущена или загружается"), ToastKind.Info);
            return;
        }
        var existing = _friends.Existing(invite);
        var text = L.T($"Друг зовёт играть: «{invite.Name}»\nMinecraft {invite.GameVersion} · {invite.Loader}, файлов с Modrinth: {invite.Files.Count}") +
                   (invite.Server is { } srv ? L.T($"\nСервер: {srv}") : "") +
                   (existing is null ? L.T("\n\nУстановить и запустить?") : L.T($"\n\nВерсия уже есть («{existing.Name}»). Проверить файлы и запустить?"));
        if (MessageBox.Show(text, "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        Mascot = "/Assets/ui/loading_think.png";
        Instance inst;
        try
        {
            inst = await _friends.InstallAsync(invite, _settings.Current.DefaultRamMb, new Progress<DownloadProgress>(Progress.Apply), _cts.Token);
        }
        catch (InstanceLimitException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Warning, 7);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Сборка друга");
            _toasts.Show(ex is OperationCanceledException ? L.T("Запуск отменён") : L.T("Не удалось поставить сборку друга: ") + ex.Message, ToastKind.Error, 7);
            Mascot = "/Assets/ui/frog_idle.png";
            return;
        }
        finally
        {
            IsBusy = false;
            Progress.Reset();
        }
        _toasts.Show(L.T($"Сборка «{inst.Name}» готова 🐸"), ToastKind.Success);
        if (invite.Server is { } server) await PlayOnServerAsync(inst, server);
        else await PlayByIdAsync(inst.Id);
    }
    private bool _recovered;
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNewYearPanel))] private CrashInfo? _crash;
    [ObservableProperty] private bool _showCrashLog;

    // ---------- Сезоны ----------
    private NewYearService? _newYear;
    private ThemeService? _themes;
    private string _playlistUrl = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowNewYearPanel))] private bool _isNewYearPeriod;
    [ObservableProperty] private bool _isNewYearTheme;
    [ObservableProperty] private bool _isSpringTheme;
    [ObservableProperty] private int _treeDays;
    [ObservableProperty] private bool _hasGarland;
    [ObservableProperty] private bool _hasStar;
    [ObservableProperty] private bool _canClaimGift;
    [ObservableProperty] private string _couponText = "";

    public bool ShowNewYearPanel => IsNewYearPeriod && Crash is null;
    public string TreeText => L.T($"🎄 Ёлка украшается за дни в лаунчере: {TreeDays} · {NewYearService.GarlandDays} — гирлянда, {NewYearService.StarDays} — звезда");

    /// <summary>Сезонные части главной (вызывается из конструктора и при смене темы).</summary>
    public void AttachSeasons(NewYearService newYear, ThemeService themes, string playlistUrl)
    {
        _newYear = newYear;
        _themes = themes;
        _playlistUrl = playlistUrl;
        themes.SeasonChanged += RefreshSeason;
        newYear.Changed += RefreshSeason;
        RefreshSeason();
    }

    private void RefreshSeason()
    {
        if (_newYear is null || _themes is null) return;
        var today = AppClock.Today;
        IsNewYearPeriod = _newYear.IsActive(today);
        IsNewYearTheme = _themes.ActiveSeason == Season.NewYear;
        IsSpringTheme = _themes.ActiveSeason == Season.Spring;
        TreeDays = _newYear.Days(today);
        HasGarland = NewYearService.HasGarland(TreeDays);
        HasStar = NewYearService.HasStar(TreeDays);
        CanClaimGift = _newYear.CanClaim(today);
        var coupon = _newYear.ActiveCoupon(today);
        CouponText = coupon > 0
            ? L.T($"Ваш новогодний купон: −{coupon}% на подписку до 10 января. Он сам попадёт в заказ при покупке.")
            : L.T("Заходите каждый день — купон на скидку растёт: 3% в первый день и +1% за каждый следующий (до 15%).");
        OnPropertyChanged(nameof(TreeText));
    }

    [RelayCommand]
    private void ClaimGift()
    {
        if (_newYear is null) return;
        var before = _newYear.ActiveCoupon(AppClock.Today);
        var now = _newYear.ClaimGift(AppClock.Today);
        _toasts.Show(now > before ? L.T($"🎁 Подарок дня! Купон теперь −{now}%") : L.T("Сегодня подарок уже получен — приходите завтра 🎄"), ToastKind.Success, 5);
        RefreshSeason();
    }

    [RelayCommand]
    private void OpenPlaylist() => Shell.Open(_playlistUrl);
    [ObservableProperty] private string? _mclogsUrl;
    [ObservableProperty] private bool _showAdBanner;
    [ObservableProperty] private string _mascot = "/Assets/ui/frog_idle.png";
    [ObservableProperty] private string _accountLabel = L.T("Аккаунт не выбран");

    public HomeViewModel(InstanceService instances, LaunchService launcher, GameAuthService accounts,
        EntitlementService entitlements, DownloadService downloads, SettingsService settings,
        DiscordPresenceService presence, ToastService toasts, PlayStatsService stats, TrayService tray, WorldService worlds,
        CrashDoctorViewModel doctor, ModToolsService modTools, ModBisectService bisect, FriendLinkService friends, SkinService skins,
        PreflightService preflight, QuestsViewModel quests, SharedPackService shared, ILogger<HomeViewModel> log)
    {
        _shared = shared;
        Quests = quests;
        _preflight = preflight;
        doctor.LaunchAnywayRequested += () =>
        {
            _skipPreflight = true;
            if (PlayCommand.CanExecute(null)) PlayCommand.Execute(null);
        };
        _skins = skins;
        _friends = friends;
        // Друг нажал «Присоединиться» в нашем статусе Discord.
        presence.JoinRequested += secret => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (FriendLinkService.Parse(new Uri("ribbix://play?" + secret)) is { } invite) _ = JoinFriendAsync(invite);
        });
        Doctor = doctor;
        _modTools = modTools;
        _bisect = bisect;
        doctor.Fixed += () => Instances.ReplaceWith(_instances.List().Select(i => i.Id == SelectedInstance?.Id ? SelectedInstance! : i));
        _worlds = worlds;
        _stats = stats;
        _tray = tray;
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
            if (E4mcRegex().Match(line) is { Success: true } host) OnWorldOpened(host.Value);
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
        Quests.Refresh();
        if (!_synced)
        {
            _synced = true;
            _ = SyncSharedPacksAsync();
        }
        if (!_recovered)
        {
            // Лаунчер закрыли посреди поиска виновника — вернуть моды.
            _recovered = true;
            foreach (var i in Instances) _bisect.RecoverIfInterrupted(i);
        }
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

    /// <summary>Сервер для автоподключения при следующем запуске (из вкладки «Серверы»).</summary>
    private string? _pendingServer;

    /// <summary>Запуск конкретной версии (из командной палитры).</summary>
    public async Task PlayByIdAsync(string id)
    {
        await LoadAsync();
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == id) ?? SelectedInstance;
        if (PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
        else _toasts.Show(L.T("Игра уже запущена или загружается"), ToastKind.Info);
    }

    /// <summary>Запустить версию и сразу подключиться к серверу.</summary>
    public async Task PlayOnServerAsync(Instance inst, string server)
    {
        await LoadAsync();
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == inst.Id) ?? SelectedInstance;
        _pendingServer = server;
        if (PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
        else
        {
            _pendingServer = null;
            _toasts.Show(L.T("Игра уже запущена или загружается"), ToastKind.Info);
        }
    }

    /// <summary>Запуск игры. Подписка здесь НЕ проверяется — играть можно всегда.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (SelectedInstance is not { } inst)
        {
            _toasts.Show(L.T("Добавьте версию в «Каталоге»"), ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Catalog));
            return;
        }

        if (_accounts.Selected is not { } account)
        {
            _toasts.Show(L.T("Добавьте аккаунт в «Профиле» — подойдёт и оффлайн-ник"), ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Profile));
            return;
        }

        // Проверка перед запуском: нашлись проблемы — показываем их вместо запуска (кнопки починки и «Играть всё равно»).
        if (!_skipPreflight)
        {
            var found = await Task.Run(() => _preflight.Check(inst, HardwareService.MaxSafeRamMb(HardwareService.Scan())));
            if (found.Count > 0)
            {
                Crash = new CrashInfo(0, Array.Empty<string>(), null);
                Doctor.ShowPreflight(inst, found);
                Mascot = "/Assets/ui/frog_surprise.png";
                return;
            }
        }
        _skipPreflight = false;

        var server = _pendingServer;
        _pendingServer = null;
        IsBusy = true;
        Crash = null;
        Doctor.Clear();
        MclogsUrl = null;
        LogLines.Clear();
        Mascot = "/Assets/ui/loading_think.png";
        _cts = new CancellationTokenSource();
        _downloads.Parallelism = _entitlements.IsAllowed(EntitlementKeys.PriorityDownload) ? 16 : 8;

        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Apply(p);
            _ = _presence.SetAsync(new PresenceState(L.T($"Готовит {inst.DisplayVersion}"),
                p.TotalBytes > 0 ? $"{p.Stage}: {p.Percent:0}%" : p.Stage, SmallImage: "download", SmallText: L.T("Загрузка")));
        });

        try
        {
            // Оффлайн-скин — ресурспаком «Ribbix Skin» (для Microsoft скин уже в профиле Minecraft).
            _skins.PrepareLaunch(inst, account);
            // Хост общей сборки: моды поменялись — обновить gist, друзья подтянут сами.
            _ = _shared.PublishIfChangedAsync(inst, CancellationToken.None);
            var session = await _launcher.LaunchAsync(inst, account,
                _entitlements.IsAllowed(EntitlementKeys.JvmAutotune), progress, _cts.Token, server);
            _instances.Save(inst);
            IsRunning = true;
            _stats.SessionStarted(inst, server);
            _tray.SetGameRunning(true);
            StartPlaytimeReminder();
            Mascot = "/Assets/ui/frog_cool.png";
            await _presence.SetAsync(new PresenceState(
                L.T($"Играет в Minecraft {inst.GameVersion}"),
                server is not null ? L.T($"На сервере {server}")
                    : inst.Loader == LoaderType.Vanilla ? inst.Name : $"{inst.Name} · {inst.Loader}",
                session.StartedAt, SmallImage: "playing", SmallText: account.Name,
                JoinSecret: server is not null ? FriendLinkService.JoinSecret(inst, server) : null));

            if (_settings.Current.CloseOnLaunch) Application.Current.MainWindow!.WindowState = WindowState.Minimized;
        }
        catch (OperationCanceledException)
        {
            _toasts.Show(L.T("Запуск отменён"));
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
            _toasts.Show(L.T("Ссылка на лог скопирована 🐸"), ToastKind.Success);
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

    /// <summary>Каждые N часов непрерывной игры — уведомление в трее (настраивается).</summary>
    private void StartPlaytimeReminder()
    {
        var hours = _settings.Current.PlaytimeReminderHours;
        if (hours <= 0) return;
        var played = 0;
        _reminder = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(hours) };
        _reminder.Tick += (_, _) =>
        {
            played += hours;
            _tray.Notify(L.T($"Вы играете уже {played} ч 🐸"), L.T("Самое время размяться, попить воды и дать глазам отдохнуть."));
        };
        _reminder.Start();
    }

    /// <summary>Автобэкап миров, в которых играли (раз в сутки): при запуске лаунчера и после выхода из игры.</summary>
    public async Task AutoBackupAsync(IEnumerable<Instance>? only = null)
    {
        try
        {
            var n = await Task.Run(() => _worlds.AutoBackupAll(only ?? _instances.List()));
            if (n > 0) _log.LogInformation("Автобэкап: сохранено миров — {Count}", n);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Автобэкап не удался");
        }
    }

    private async void OnGameExited(GameSession s, CrashInfo? crash)
    {
        if (s.IsDiagnostic) return; // поиск виновника / замер FPS — свои запуски
        IsRunning = false;
        Quests.Refresh();
        _reminder?.Stop();
        _reminder = null;
        _tray.SetGameRunning(false);
        _stats.SessionEnded();
        _ = AutoBackupAsync(new[] { s.Instance });
        Crash = crash;
        if (crash is not null) _ = Doctor.AnalyzeAsync(s.Instance, crash);
        else _modTools.MarkVerified(s.Instance);
        Mascot = crash is null ? "/Assets/ui/frog_happy.png" : "/Assets/ui/frog_sad.png";
        if (crash is not null) _toasts.Show(L.T($"Игра упала (код {crash.ExitCode}). «Доктор крашей» на главной уже разбирается 🩺"), ToastKind.Error, 7);
        if (Application.Current.MainWindow is { WindowState: WindowState.Minimized } w) w.WindowState = WindowState.Normal;
        await _presence.SetIdleAsync();
    }

    private void UpdateTierUi() =>
        ShowAdBanner = !_entitlements.IsAllowed(EntitlementKeys.NoAds);

    private void UpdateAccount() =>
        AccountLabel = _accounts.Selected is { } a ? $"{a.Name} · {a.TypeLabel}" : L.T("Аккаунт не выбран");
}
