using RibbixLauncher.Core;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RibbixLauncher.Services;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

public enum Page { Home, Catalog, Modpacks, Servers, Java, Settings, Profile, Subscription, Help, Skins }

/// <summary>Сообщение «перейти на страницу» (например, из апселл-баннера).</summary>
public sealed record NavigateMessage(Page Page);

public partial class MainViewModel : ObservableObject, IRecipient<NavigateMessage>
{
    private readonly EntitlementService _entitlements;
    private readonly SessionStore _session;
    private readonly GameAuthService _accounts;
    private readonly ThemeService _themes;
    private readonly SettingsService _settings;
    private readonly DiscordPresenceService _presence;
    private readonly ILogger<MainViewModel> _log;
    private readonly UpdateService _updates;
    private readonly TrayService _tray;
    private readonly Dictionary<Page, object> _pages;

    /// <summary>Попросить окно поставить фокус в поле поиска текущей страницы (Ctrl+K).</summary>
    public event Action? SearchRequested;

    [ObservableProperty] private string? _updateBanner;

    public ToastService Toasts { get; }
    public OnboardingViewModel Onboarding { get; }
    public CommandPaletteViewModel Palette { get; }

    // ---------- Сезоны (v2.4) ----------
    private readonly NewYearService _newYear;
    private readonly SoundService _sounds;
    private System.Windows.Threading.DispatcherTimer? _seasonTimer;
    [ObservableProperty] private Season? _season;
    [ObservableProperty] private bool _effectsPaused;
    [ObservableProperty] private bool _fireworksVisible;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private Page _selectedPage = Page.Home;
    [ObservableProperty] private string _displayName = L.T("Гость");
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _tierName = "Tadpole";
    [ObservableProperty] private string? _graceBanner;
    [ObservableProperty] private bool _verificationFailed;

    public MainViewModel(
        HomeViewModel home, InstancesViewModel catalog, ModpacksViewModel modpacks, ServersViewModel servers, JavaViewModel java, SettingsViewModel settingsVm,
        ProfileViewModel profile, SubscriptionViewModel subscription, AboutViewModel about, SkinsViewModel skins,
        EntitlementService entitlements, SessionStore session, GameAuthService accounts, ThemeService themes,
        SettingsService settings, DiscordPresenceService presence, ToastService toasts, UpdateService updates,
        OnboardingViewModel onboarding, TrayService tray, InstanceService instanceStore, VersionService versions,
        ModrinthService modrinth, NewYearService newYear, SoundService sounds,
        Microsoft.Extensions.Options.IOptions<Core.LauncherOptions> options, ILogger<MainViewModel> log)
    {
        home.AttachSeasons(newYear, themes, options.Value.NewYearPlaylistUrl);
        _newYear = newYear;
        _sounds = sounds;
        themes.SeasonChanged += () => Season = themes.ActiveSeason;
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.IsRunning)) UpdateEffectsPaused();
        };
        Palette = new CommandPaletteViewModel(this, instanceStore, versions, modrinth);
        _tray = tray;
        Onboarding = onboarding;
        _updates = updates;
        updates.ReadyChanged += () => Application.Current.Dispatcher.Invoke(() =>
            UpdateBanner = updates.Ready is { } u ? L.T($"Готово обновление Ribbix {u.Version}{(u.IsPrerelease ? " (beta)" : "")}") : null);
        _entitlements = entitlements;
        _session = session;
        _accounts = accounts;
        _themes = themes;
        _settings = settings;
        _presence = presence;
        _log = log;
        Toasts = toasts;
        _pages = new()
        {
            [Page.Home] = home, [Page.Catalog] = catalog, [Page.Modpacks] = modpacks, [Page.Servers] = servers, [Page.Java] = java, [Page.Settings] = settingsVm,
            [Page.Profile] = profile, [Page.Subscription] = subscription, [Page.Help] = about, [Page.Skins] = skins,
        };
        _currentPage = home;

        WeakReferenceMessenger.Default.Register(this);
        entitlements.Changed += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
        session.SessionChanged += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
        session.SessionExpired += () => toasts.Show(L.T("Сессия Ribbix завершена. Войдите заново."), ToastKind.Warning, 6);
        accounts.Changed += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
    }

    public async Task InitializeAsync()
    {
        _presence.Enabled = _settings.Current.DiscordPresence;
        IntroduceSeasons();
        _themes.Apply(_settings.Current.Theme);
        Season = _themes.ActiveSeason;
        UpdateEffectsPaused();
        _newYear.RecordLogin(AppClock.Today);
        StartSeasonTimer();
        if (_settings.Current.StartupSound && Season is { } season) _sounds.PlayStartup(season);
        RefreshHeader();
        _entitlements.Start();
        await _presence.SetIdleAsync();
        await ((HomeViewModel)_pages[Page.Home]).LoadAsync();
        Onboarding.ShowIfFirstRun();
        _ = CheckUpdatesAsync();
        _ = CheckNewMinecraftAsync();
        _ = ((HomeViewModel)_pages[Page.Home]).AutoBackupAsync();
    }

    /// <summary>v2.4: у кого стояло «Болото» по умолчанию — один раз включаем сезонную тему.</summary>
    private void IntroduceSeasons()
    {
        if (_settings.Current.SeasonsIntroduced) return;
        _settings.Current.SeasonsIntroduced = true;
        if (_settings.Current.Theme == "swamp")
        {
            _settings.Current.Theme = Seasons.AutoKey;
            Toasts.Show(L.T("Включены сезонные темы 🌸☀️🍂❄️ Вернуть «Болото» можно в «Настройках» → «Темы»."), ToastKind.Info, 8);
        }
        _settings.Save();
    }

    public void UpdateEffectsPaused() =>
        EffectsPaused = !_settings.Current.SeasonEffects || ((HomeViewModel)_pages[Page.Home]).IsRunning;

    /// <summary>Раз в 20 секунд: смена сезона в полночь (для «Сезонной (авто)») и новогодний фейерверк.</summary>
    private void StartSeasonTimer()
    {
        _seasonTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        var day = AppClock.Today;
        _seasonTimer.Tick += (_, _) =>
        {
            if (AppClock.Today != day)
            {
                day = AppClock.Today;
                _themes.Apply(_settings.Current.Theme);
                _newYear.RecordLogin(day);
            }
            if (NewYearService.IsFireworksTime(AppClock.Now) && !_newYear.State.FireworksShown)
            {
                _newYear.State.FireworksShown = true;
                ShowFireworks();
            }
        };
        _seasonTimer.Start();
    }

    [RelayCommand]
    public void ShowFireworks()
    {
        FireworksVisible = true;
        Toasts.Show(L.T("С Новым годом! 🎆🐸"), ToastKind.Success, 8);
        // Две минуты праздника, дальше — сам.
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        t.Tick += (_, _) => { FireworksVisible = false; t.Stop(); };
        t.Start();
    }

    [RelayCommand]
    private void HideFireworks() => FireworksVisible = false;

    /// <summary>Через 5 с после старта — тихая проверка и загрузка обновления (только для установленной копии).</summary>
    private async Task CheckUpdatesAsync()
    {
        if (!_settings.Current.AutoUpdate || !UpdateService.IsInstalledCopy) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        try { await _updates.CheckAndDownloadAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            _log.LogInformation("Проверка обновлений не удалась: {Message}", ex.Message);
        }
    }

    /// <summary>Уведомление в трее, когда вышла новая версия Minecraft (сравниваем с последней увиденной).</summary>
    private async Task CheckNewMinecraftAsync()
    {
        try
        {
            var versions = App.Services.GetService(typeof(VersionService)) as VersionService;
            if (versions is null) return;
            var latest = (await versions.GetManifestAsync()).Latest.Release;
            var seen = _settings.Current.LastSeenMcRelease;
            _settings.Current.LastSeenMcRelease = latest;
            _settings.Save();
            if (seen is not null && seen != latest)
                _tray.Notify(L.T($"Вышла Minecraft {latest}! 🎉"), L.T("Создайте новую версию в «Каталоге» или дождитесь обновления сборок."));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException) { }
    }

    [RelayCommand]
    private void ApplyUpdate()
    {
        if (!_updates.ApplyAndRestart()) Toasts.Show(L.T("Файл обновления не найден — проверим заново при следующем запуске"), ToastKind.Warning);
    }

    [RelayCommand]
    private void SkipUpdate() => _updates.Skip();

    /// <summary>Выполнить действие горячей клавиши.</summary>
    public void RunHotkey(string action)
    {
        switch (action)
        {
            case "play":
            case "minimizePlay":
                var home = (HomeViewModel)_pages[Page.Home];
                SelectedPage = Page.Home;
                if (!home.PlayCommand.CanExecute(null)) return;
                if (action == "minimizePlay") (Application.Current.MainWindow as Views.MainWindow)?.HideToTray();
                _ = home.PlayCommand.ExecuteAsync(null);
                break;
            case "new":
                SelectedPage = Page.Catalog;
                ((InstancesViewModel)_pages[Page.Catalog]).StartCreateCommand.Execute(null);
                break;
            case "search":
                if (Palette.IsOpen) Palette.Close();
                else Palette.Open();
                break;
            case "refresh":
                if (CurrentPage is IRefreshable r) _ = r.RefreshPageAsync();
                break;
            case "settings":
                SelectedPage = Page.Settings;
                break;
        }
    }

    /// <summary>Поле поиска текущей страницы (бывший Ctrl+K, теперь — пункт палитры).</summary>
    public void RequestPageSearch() => SearchRequested?.Invoke();

    /// <summary>Перейти на страницу и, когда она откроется, сделать с её моделью что-то ещё.</summary>
    public void Go<T>(Page page, Action<T> then) where T : class
    {
        SelectedPage = page;
        Application.Current.Dispatcher.BeginInvoke(() => then((T)_pages[page]), System.Windows.Threading.DispatcherPriority.Background);
    }

    public Models.Instance? HomeInstance => ((HomeViewModel)_pages[Page.Home]).SelectedInstance;

    public void PlayInstance(string id)
    {
        var home = (HomeViewModel)_pages[Page.Home];
        SelectedPage = Page.Home;
        _ = home.PlayByIdAsync(id);
    }

    /// <summary>В поиске на странице без поля поиска — открываем «Сборки».</summary>
    public void OpenDefaultSearch() => SelectedPage = Page.Modpacks;

    partial void OnSelectedPageChanged(Page value)
    {
        CurrentPage = _pages[value];
        _ = _presence.SetAsync(value switch
        {
            Page.Subscription => new PresenceState(L.T("В лаунчере"), L.T("Смотрит тарифы 👑")),
            Page.Catalog => new PresenceState(L.T("В лаунчере"), L.T("Настраивает версии и моды")),
            Page.Modpacks => new PresenceState(L.T("В лаунчере"), L.T("Выбирает сборку с Modrinth")),
            Page.Servers => new PresenceState(L.T("В лаунчере"), L.T("Выбирает сервер")),
            Page.Java => new PresenceState(L.T("В лаунчере"), L.T("Настраивает Java")),
            _ => new PresenceState(L.T("В лаунчере"), L.T("Выбирает, во что поиграть 🐸")),
        });
        if (CurrentPage is IPageActivated p) _ = ActivateAsync(p);
    }

    public void Receive(NavigateMessage message) => SelectedPage = message.Page;

    private async Task ActivateAsync(IPageActivated page)
    {
        try { await page.OnActivatedAsync(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при открытии страницы {Page}", SelectedPage);
            Toasts.Show(L.T("Не удалось загрузить страницу: ") + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void Navigate(Page page) => SelectedPage = page;

    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    /// <summary>«Выход» в меню — настоящее закрытие, а не сворачивание в трей.</summary>
    [RelayCommand]
    private void Exit()
    {
        _tray.MarkExiting();
        Application.Current.MainWindow?.Close();
    }

    public void StopGame()
    {
        var home = (HomeViewModel)_pages[Page.Home];
        if (home.StopCommand.CanExecute(null)) home.StopCommand.Execute(null);
    }

    /// <summary>ribbix://billing/success?sub_id=... — возврат после оплаты.</summary>
    public void HandleDeepLink(Uri uri)
    {
        _log.LogInformation("Deeplink {Host}{Path}", uri.Host, uri.AbsolutePath);
        if (uri.Host == "pack" && UrlScheme.Query(uri, "gist") is { } gist && SharedPackService.IsGistId(gist))
        {
            SelectedPage = Page.Home;
            _ = ((HomeViewModel)_pages[Page.Home]).JoinSharedPackAsync(gist);
        }
        else if (FriendLinkService.Parse(uri) is { } invite)
        {
            SelectedPage = Page.Home;
            _ = ((HomeViewModel)_pages[Page.Home]).JoinFriendAsync(invite);
        }
        else if (uri.Host == "billing" && uri.AbsolutePath.StartsWith("/success", StringComparison.Ordinal))
        {
            SelectedPage = Page.Subscription;
            _ = ((SubscriptionViewModel)_pages[Page.Subscription]).WaitForActivationAsync(UrlScheme.Query(uri, "sub_id"));
        }
        else if (uri.Host == "billing")
        {
            SelectedPage = Page.Subscription;
            Toasts.Show(L.T("Оплата не завершена"), ToastKind.Warning);
        }
    }

    private void RefreshHeader()
    {
        var snap = _entitlements.Current;
        DisplayName = _entitlements.SignedInName is { Length: > 0 } nick ? nick : _accounts.Selected?.Name ?? L.T("Гость");
        IsAdmin = _entitlements.IsAdmin;
        TierName = TierNames.Display(snap.Tier);
        VerificationFailed = _entitlements.VerificationFailed;
        GraceBanner = _entitlements.GraceDaysLeft is { } days
            ? L.T($"Подписка истекла. Продлите. Осталось {days} {Plural(days, "день", "дня", "дней")}.")
            : null;
        // Подписка кончилась, а тема была премиальной — вернётся бесплатная.
        _themes.Apply(_settings.Current.Theme);
    }

    internal static string Plural(int n, string one, string few, string many) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (>= 2 and <= 4, < 12 or > 14) => few,
            _ => many,
        };
}

/// <summary>Страница, которой нужно обновиться при открытии.</summary>
public interface IPageActivated
{
    Task OnActivatedAsync();
}

/// <summary>Страница, которую можно обновить клавишей F5.</summary>
public interface IRefreshable
{
    Task RefreshPageAsync();
}

/// <summary>Отображение прогресса загрузки в UI.</summary>
public partial class ProgressState : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _details = "";

    public void Apply(DownloadProgress p)
    {
        IsActive = true;
        Stage = p.Stage;
        IsIndeterminate = p.TotalBytes == 0 && p.TotalFiles == 0;
        Percent = p.Percent;
        Details = p.TotalBytes > 0
            ? L.T($"{p.Percent:0}% · {Core.Fmt.Bytes(p.DoneBytes)} из {Core.Fmt.Bytes(p.TotalBytes)} · {Core.Fmt.Bytes(p.BytesPerSecond)}/с · осталось {Core.Fmt.Eta(p.Eta)}")
            : p.TotalFiles > 0 ? L.T($"{p.DoneFiles} из {p.TotalFiles}") : "";
    }

    public void Reset()
    {
        IsActive = false;
        Percent = 0;
        Stage = Details = "";
    }
}

/// <summary>Подпись фичи для карточек тарифов.</summary>
public static class EntitlementLabels
{
    public static string? Label(string key) => key switch
    {
        EntitlementKeys.NoAds => L.T("Без рекламы"),
        EntitlementKeys.CloudSync => L.T("Синхронизация настроек"),
        EntitlementKeys.PremiumThemes3 => L.T("3 премиум-темы"),
        EntitlementKeys.PremiumThemesAll => L.T("Все темы"),
        EntitlementKeys.Instances50 => L.T("До 50 версий"),
        EntitlementKeys.InstancesUnlimited => L.T("Безлимит версий"),
        EntitlementKeys.CloudBackup10Gb => L.T("Облачные бэкапы 10 ГБ"),
        EntitlementKeys.CloudBackup50Gb => L.T("Облачные бэкапы 50 ГБ"),
        EntitlementKeys.JvmAutotune => L.T("Автонастройка JVM"),
        EntitlementKeys.PriorityDownload => L.T("Приоритетная загрузка"),
        EntitlementKeys.CrashAnalytics => L.T("Аналитика крэшей"),
        EntitlementKeys.EarlyAccess => L.T("Ранний доступ"),
        EntitlementKeys.PersonalSupport => L.T("Личная поддержка"),
        EntitlementKeys.RoadmapVote => L.T("Голос в роадмапе"),
        EntitlementKeys.ExclusiveTheme => L.T("Эксклюзивная тема"),
        EntitlementKeys.Base => L.T("25 версий, модлоадеры и каталог модов"),
        _ => null,
    };
}

/// <summary>Наблюдаемая коллекция с массовой заменой.</summary>
public static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> c, IEnumerable<T> items)
    {
        c.Clear();
        foreach (var i in items) c.Add(i);
    }
}
