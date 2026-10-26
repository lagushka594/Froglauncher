using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Services;
using Frog.Shared;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.ViewModels;

public enum Page { Home, Catalog, Modpacks, Java, Settings, Profile, Subscription, Help }

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
    private readonly Dictionary<Page, object> _pages;

    public ToastService Toasts { get; }

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private Page _selectedPage = Page.Home;
    [ObservableProperty] private string _displayName = "Гость";
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _tierName = "Tadpole";
    [ObservableProperty] private string? _graceBanner;
    [ObservableProperty] private bool _verificationFailed;

    public MainViewModel(
        HomeViewModel home, InstancesViewModel catalog, ModpacksViewModel modpacks, JavaViewModel java, SettingsViewModel settingsVm,
        ProfileViewModel profile, SubscriptionViewModel subscription, AboutViewModel about,
        EntitlementService entitlements, SessionStore session, GameAuthService accounts, ThemeService themes,
        SettingsService settings, DiscordPresenceService presence, ToastService toasts, ILogger<MainViewModel> log)
    {
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
            [Page.Home] = home, [Page.Catalog] = catalog, [Page.Modpacks] = modpacks, [Page.Java] = java, [Page.Settings] = settingsVm,
            [Page.Profile] = profile, [Page.Subscription] = subscription, [Page.Help] = about,
        };
        _currentPage = home;

        WeakReferenceMessenger.Default.Register(this);
        entitlements.Changed += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
        session.SessionChanged += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
        session.SessionExpired += () => toasts.Show("Сессия FrogLauncher завершена. Войдите заново.", ToastKind.Warning, 6);
        accounts.Changed += () => Application.Current.Dispatcher.Invoke(RefreshHeader);
    }

    public async Task InitializeAsync()
    {
        _presence.Enabled = _settings.Current.DiscordPresence;
        _themes.Apply(_settings.Current.Theme);
        RefreshHeader();
        _entitlements.Start();
        await _presence.SetIdleAsync();
        await ((HomeViewModel)_pages[Page.Home]).LoadAsync();
    }

    partial void OnSelectedPageChanged(Page value)
    {
        CurrentPage = _pages[value];
        _ = _presence.SetAsync(value switch
        {
            Page.Subscription => new PresenceState("В лаунчере", "Смотрит тарифы 👑"),
            Page.Catalog => new PresenceState("В лаунчере", "Настраивает версии и моды"),
            Page.Modpacks => new PresenceState("В лаунчере", "Выбирает сборку с Modrinth"),
            Page.Java => new PresenceState("В лаунчере", "Настраивает Java"),
            _ => new PresenceState("В лаунчере", "Выбирает, во что поиграть 🐸"),
        });
        if (CurrentPage is IPageActivated p) _ = p.OnActivatedAsync();
    }

    public void Receive(NavigateMessage message) => SelectedPage = message.Page;

    [RelayCommand]
    private void Navigate(Page page) => SelectedPage = page;

    [RelayCommand]
    private static void Exit() => Application.Current.MainWindow?.Close();

    /// <summary>froglauncher://billing/success?sub_id=... — возврат после оплаты.</summary>
    public void HandleDeepLink(Uri uri)
    {
        _log.LogInformation("Deeplink {Host}{Path}", uri.Host, uri.AbsolutePath);
        if (uri.Host == "billing" && uri.AbsolutePath.StartsWith("/success", StringComparison.Ordinal))
        {
            SelectedPage = Page.Subscription;
            _ = ((SubscriptionViewModel)_pages[Page.Subscription]).WaitForActivationAsync(UrlScheme.Query(uri, "sub_id"));
        }
        else if (uri.Host == "billing")
        {
            SelectedPage = Page.Subscription;
            Toasts.Show("Оплата не завершена", ToastKind.Warning);
        }
    }

    private void RefreshHeader()
    {
        var snap = _entitlements.Current;
        DisplayName = _entitlements.SignedInName is { Length: > 0 } nick ? nick : _accounts.Selected?.Name ?? "Гость";
        IsAdmin = _entitlements.IsAdmin;
        TierName = TierNames.Display(snap.Tier);
        VerificationFailed = _entitlements.VerificationFailed;
        GraceBanner = _entitlements.GraceDaysLeft is { } days
            ? $"Подписка истекла. Продлите. Осталось {days} {Plural(days, "день", "дня", "дней")}."
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
            ? $"{p.Percent:0}% · {Core.Fmt.Bytes(p.DoneBytes)} из {Core.Fmt.Bytes(p.TotalBytes)} · {Core.Fmt.Bytes(p.BytesPerSecond)}/с · осталось {Core.Fmt.Eta(p.Eta)}"
            : p.TotalFiles > 0 ? $"{p.DoneFiles} из {p.TotalFiles}" : "";
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
        EntitlementKeys.NoAds => "Без рекламы",
        EntitlementKeys.CloudSync => "Синхронизация настроек",
        EntitlementKeys.PremiumThemes3 => "3 премиум-темы",
        EntitlementKeys.PremiumThemesAll => "Все темы",
        EntitlementKeys.Instances50 => "До 50 версий",
        EntitlementKeys.InstancesUnlimited => "Безлимит версий",
        EntitlementKeys.CloudBackup10Gb => "Облачные бэкапы 10 ГБ",
        EntitlementKeys.CloudBackup50Gb => "Облачные бэкапы 50 ГБ",
        EntitlementKeys.JvmAutotune => "Автонастройка JVM",
        EntitlementKeys.PriorityDownload => "Приоритетная загрузка",
        EntitlementKeys.CrashAnalytics => "Аналитика крэшей",
        EntitlementKeys.EarlyAccess => "Ранний доступ",
        EntitlementKeys.PersonalSupport => "Личная поддержка",
        EntitlementKeys.RoadmapVote => "Голос в роадмапе",
        EntitlementKeys.ExclusiveTheme => "Эксклюзивная тема",
        EntitlementKeys.Base => "25 версий, модлоадеры и каталог модов",
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
