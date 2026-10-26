using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Frog.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrogLauncher.ViewModels;

/// <summary>«Каталог»: версии игрока (инстансы), добавление (Minecraft + модлоадер), настройки.</summary>
public partial class InstancesViewModel(
    InstanceService instances,
    VersionService versions,
    ModLoaderService loaders,
    EntitlementService entitlements,
    SettingsService settings,
    ModrinthService modrinth,
    ToastService toasts,
    ILogger<InstancesViewModel> log) : ObservableObject, IPageActivated
{
    private IReadOnlyList<ManifestEntry> _all = Array.Empty<ManifestEntry>();

    public ObservableCollection<Instance> Instances { get; } = new();
    public ObservableCollection<ManifestEntry> Versions { get; } = new();
    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();
    public IReadOnlyList<LoaderType> Loaders { get; } = Enum.GetValues<LoaderType>();

    [ObservableProperty] private Instance? _selected;
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private ManifestEntry? _newVersion;
    [ObservableProperty] private LoaderType _newLoader = LoaderType.Vanilla;
    [ObservableProperty] private LoaderVersion? _newLoaderVersion;
    [ObservableProperty] private bool _showRelease = true;
    [ObservableProperty] private bool _showSnapshot;
    [ObservableProperty] private bool _showOld;
    [ObservableProperty] private bool _loadingLoaders;
    [ObservableProperty] private string? _upsell;
    [ObservableProperty] private string _limitText = "";

    public bool CanAutotune => entitlements.IsAllowed(EntitlementKeys.JvmAutotune);

    public async Task OnActivatedAsync()
    {
        Instances.ReplaceWith(instances.List());
        Selected ??= Instances.FirstOrDefault();
        LimitText = entitlements.MaxInstances is { } max
            ? $"{Instances.Count} из {max} · {TierNames.Display(entitlements.Tier)}"
            : $"{Instances.Count} · без ограничений";
        OnPropertyChanged(nameof(CanAutotune));

        if (_all.Count == 0)
        {
            try
            {
                _all = (await versions.GetManifestAsync()).Versions;
                ApplyFilter();
            }
            catch (Exception ex)
            {
                toasts.Show("Не удалось загрузить список версий: " + ex.Message, ToastKind.Error);
            }
        }
    }

    partial void OnShowReleaseChanged(bool value) => ApplyFilter();
    partial void OnShowSnapshotChanged(bool value) => ApplyFilter();
    partial void OnShowOldChanged(bool value) => ApplyFilter();
    partial void OnNewVersionChanged(ManifestEntry? value) => _ = LoadLoaderVersionsAsync();
    partial void OnNewLoaderChanged(LoaderType value) => _ = LoadLoaderVersionsAsync();

    private void ApplyFilter()
    {
        Versions.ReplaceWith(_all.Where(v =>
            (ShowRelease && v.IsRelease) || (ShowSnapshot && v.IsSnapshot) || (ShowOld && v.IsOld)));
        NewVersion ??= Versions.FirstOrDefault();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        NewLoaderVersion = null;
        if (NewVersion is null || NewLoader == LoaderType.Vanilla) return;

        LoadingLoaders = true;
        try
        {
            var list = await loaders.GetVersionsAsync(NewLoader, NewVersion.Id, CancellationToken.None);
            LoaderVersions.ReplaceWith(list);
            NewLoaderVersion = list.FirstOrDefault(l => l.Recommended) ?? list.FirstOrDefault();
            if (list.Count == 0) toasts.Show($"{NewLoader} не поддерживает {NewVersion.Id}", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Список версий {Loader}", NewLoader);
            toasts.Show($"{NewLoader}: не удалось получить версии", ToastKind.Error);
        }
        finally
        {
            LoadingLoaders = false;
        }
    }

    [RelayCommand]
    private void StartCreate()
    {
        // Мягкий апселл: подсказываем заранее, а не после заполнения формы.
        if (entitlements.MaxInstances is { } max && Instances.Count >= max)
        {
            Upsell = new InstanceLimitException(entitlements.Tier, max).Message;
            return;
        }

        Upsell = null;
        NewName = "";
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (NewVersion is null) return;
        try
        {
            var inst = instances.Create(NewName, NewVersion.Id, NewLoader,
                NewLoader == LoaderType.Vanilla ? null : NewLoaderVersion?.Version, settings.Current.DefaultRamMb);
            IsCreating = false;
            await OnActivatedAsync();
            Selected = Instances.FirstOrDefault(i => i.Id == inst.Id);
            toasts.Show($"Версия «{inst.Name}» добавлена. Жмите «Играть» на главной 🐸", ToastKind.Success);
        }
        catch (InstanceLimitException ex)
        {
            Upsell = ex.Message;
            IsCreating = false;
        }
    }

    [RelayCommand]
    private static void Compare() => WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));

    [RelayCommand]
    private void Save()
    {
        if (Selected is null) return;
        instances.Save(Selected);
        toasts.Show("Настройки версии сохранены", ToastKind.Success);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var r = MessageBox.Show($"Удалить «{Selected.Name}» вместе с мирами и модами?", "FrogLauncher",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        instances.Delete(Selected);
        Selected = null;
        await OnActivatedAsync();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected is null) return;
        System.IO.Directory.CreateDirectory(Selected.GameDirectory);
        Shell.Open(Selected.GameDirectory);
    }

    [RelayCommand]
    private void PickJava()
    {
        if (Selected is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe", Title = "Выберите Java" };
        if (dlg.ShowDialog() == true)
        {
            Selected.JavaPath = dlg.FileName;
            Reselect();
        }
    }

    [RelayCommand]
    private void ResetJava()
    {
        if (Selected is null) return;
        Selected.JavaPath = null;
        Reselect();
    }

    /// <summary>Instance — простой POCO: чтобы форма перечитала поля, переназначаем выбор.</summary>
    private void Reselect()
    {
        var s = Selected;
        Selected = null;
        Selected = s;
    }

    // ---------------- Моды (каталог Modrinth, бесплатно и без лимитов) ----------------

    public ObservableCollection<ModFileItem> ModFiles { get; } = new();
    public ObservableCollection<ModHitItem> ModResults { get; } = new();

    [ObservableProperty] private bool _showModCatalog;
    [ObservableProperty] private string _modQuery = "";
    [ObservableProperty] private bool _isSearchingMods;
    [ObservableProperty] private string? _modsHint;

    public bool SelectedHasLoader => Selected is { Loader: not LoaderType.Vanilla };

    partial void OnSelectedChanged(Instance? value)
    {
        ShowModCatalog = false;
        ModResults.Clear();
        OnPropertyChanged(nameof(SelectedHasLoader));
        RefreshModFiles();
    }

    /// <summary>Все jar в папке mods: из каталога — с названием и иконкой, добавленные вручную — по имени файла.</summary>
    private void RefreshModFiles()
    {
        ModFiles.Clear();
        if (Selected is not { } inst) return;
        var dir = System.IO.Path.Combine(inst.GameDirectory, "mods");
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var file in System.IO.Directory.GetFiles(dir, "*.jar").Select(System.IO.Path.GetFileName).OfType<string>().Order())
        {
            var meta = inst.InstalledMods.FirstOrDefault(m => m.FileName == file);
            ModFiles.Add(new ModFileItem(file, meta?.Title ?? file, meta?.IconUrl, meta?.IsDependency == true));
        }
        ModsHint = ModFiles.Count == 0 ? "Модов пока нет — найдите их в каталоге." : null;
    }

    [RelayCommand]
    private async Task ToggleModCatalogAsync()
    {
        if (!SelectedHasLoader)
        {
            toasts.Show("Для модов нужна версия с модлоадером (Fabric, Quilt, Forge, NeoForge)", ToastKind.Info, 6);
            return;
        }
        ShowModCatalog = !ShowModCatalog;
        if (ShowModCatalog && ModResults.Count == 0) await SearchModsAsync();
    }

    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (Selected is not { } inst) return;
        IsSearchingMods = true;
        try
        {
            var hits = await modrinth.SearchAsync(inst, ModQuery, 0, CancellationToken.None);
            ModResults.ReplaceWith(hits.Select(h => new ModHitItem(h, inst.InstalledMods.Any(m => m.ProjectId == h.ProjectId))));
            if (hits.Count == 0) toasts.Show($"Ничего не нашлось для {inst.GameVersion} · {inst.Loader}", ToastKind.Info);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            toasts.Show(ex is ModsException ? ex.Message : "Modrinth недоступен, попробуйте позже", ToastKind.Error);
        }
        finally
        {
            IsSearchingMods = false;
        }
    }

    [RelayCommand]
    private async Task InstallModAsync(ModHitItem? item)
    {
        if (item is null || Selected is not { } inst || item.IsInstalled || item.IsInstalling) return;
        item.IsInstalling = true;
        try
        {
            var added = await modrinth.InstallAsync(inst, item.Hit.ProjectId, CancellationToken.None);
            instances.Save(inst);
            item.IsInstalled = true;
            foreach (var r in ModResults.Where(r => added.Any(a => a.ProjectId == r.Hit.ProjectId))) r.IsInstalled = true;
            RefreshModFiles();
            var deps = added.Count(a => a.IsDependency);
            toasts.Show(deps > 0 ? $"«{item.Hit.Title}» установлен + {deps} зависимост(и) 🐸" : $"«{item.Hit.Title}» установлен 🐸", ToastKind.Success);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            log.LogWarning(ex, "Установка мода {Project}", item.Hit.ProjectId);
            toasts.Show(ex is ModsException ? ex.Message : "Не удалось скачать мод: " + ex.Message, ToastKind.Error, 6);
        }
        finally
        {
            item.IsInstalling = false;
        }
    }

    [RelayCommand]
    private void RemoveMod(ModFileItem? mod)
    {
        if (mod is null || Selected is not { } inst) return;
        modrinth.Remove(inst, mod.FileName);
        instances.Save(inst);
        foreach (var r in ModResults) r.IsInstalled = inst.InstalledMods.Any(m => m.ProjectId == r.Hit.ProjectId);
        RefreshModFiles();
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Selected is null) return;
        var dir = System.IO.Path.Combine(Selected.GameDirectory, "mods");
        System.IO.Directory.CreateDirectory(dir);
        Shell.Open(dir);
    }
}

/// <summary>Файл мода в папке mods.</summary>
public sealed record ModFileItem(string FileName, string Title, string? IconUrl, bool IsDependency)
{
    public string Subtitle => IsDependency ? "зависимость · " + FileName : FileName;
}

/// <summary>Результат поиска в каталоге модов.</summary>
public partial class ModHitItem(ModHit hit, bool installed) : ObservableObject
{
    public ModHit Hit { get; } = hit;
    public string DownloadsText => Hit.Downloads >= 1_000_000 ? $"{Hit.Downloads / 1_000_000.0:0.#} млн загрузок"
        : Hit.Downloads >= 1000 ? $"{Hit.Downloads / 1000.0:0.#} тыс. загрузок" : $"{Hit.Downloads} загрузок";

    [ObservableProperty] private bool _isInstalled = installed;
    [ObservableProperty] private bool _isInstalling;
}

/// <summary>«Java»: найденные рантаймы и загрузка Temurin 8/17/21.</summary>
public partial class JavaViewModel(JavaService java, DiscordPresenceService presence, ToastService toasts) : ObservableObject, IPageActivated
{
    public ObservableCollection<JavaRuntime> Runtimes { get; } = new();
    public ProgressState Progress { get; } = new();

    [ObservableProperty] private bool _isBusy;

    public async Task OnActivatedAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Runtimes.ReplaceWith(await java.DetectAsync(force: true));
    }

    [RelayCommand]
    private async Task DownloadAsync(string major)
    {
        if (IsBusy) return;
        IsBusy = true;
        var m = int.Parse(major);
        try
        {
            await presence.SetAsync(new PresenceState("В лаунчере", $"Устанавливает Java {m}", SmallImage: "java", SmallText: "Java"));
            await java.DownloadTemurinAsync(m, new Progress<DownloadProgress>(Progress.Apply), CancellationToken.None);
            toasts.Show($"Java {m} установлена ☕", ToastKind.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            toasts.Show($"Java {m}: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            Progress.Reset();
            await presence.SetIdleAsync();
        }
    }
}

/// <summary>«Профиль»: игровые аккаунты (оффлайн/Microsoft) и аккаунт FrogLauncher для подписки.</summary>
public partial class ProfileViewModel(
    GameAuthService accounts,
    FrogApiClient api,
    SessionStore session,
    EntitlementService entitlements,
    DiscordAuthService discord,
    ToastService toasts) : ObservableObject, IPageActivated
{
    private CancellationTokenSource? _msCts;
    private CancellationTokenSource? _discordCts;

    /// <summary>Тариф берётся из ролей Discord — вместо аккаунта FrogLauncher показываем вход через Discord.</summary>
    public bool UsesDiscord => entitlements.UsesDiscord;

    [ObservableProperty] private bool _discordLoggedIn;
    [ObservableProperty] private bool _discordWaiting;
    [ObservableProperty] private string _discordName = "";
    [ObservableProperty] private string? _discordAvatar;
    [ObservableProperty] private string? _discordHint;

    public ObservableCollection<GameAccount> Accounts { get; } = new();

    [ObservableProperty] private GameAccount? _selectedAccount;
    [ObservableProperty] private string _offlineNick = "";
    [ObservableProperty] private DeviceCodeInfo? _deviceCode;
    [ObservableProperty] private bool _msWaiting;

    [ObservableProperty] private string _login = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private bool _registerMode;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _launcherNick = "";
    [ObservableProperty] private string _tierName = "";
    [ObservableProperty] private bool _isAdmin;

    public Task OnActivatedAsync()
    {
        var f = accounts.Load();
        Accounts.ReplaceWith(f.Accounts);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == f.SelectedId) ?? Accounts.FirstOrDefault();
        IsLoggedIn = session.IsLoggedIn;
        LauncherNick = session.Current?.Nick ?? "";
        TierName = TierNames.Display(entitlements.Tier);
        IsAdmin = entitlements.IsAdmin;
        DiscordLoggedIn = discord.IsLoggedIn;
        DiscordName = discord.Current?.DisplayName ?? "";
        DiscordAvatar = discord.Current?.AvatarUrl;
        DiscordHint = entitlements.NotGuildMember
            ? "Вы не состоите на нашем Discord-сервере — вступите, чтобы тариф по роли включился."
            : null;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoginDiscordAsync()
    {
        _discordCts?.Cancel();
        _discordCts = new CancellationTokenSource();
        DiscordWaiting = true;
        try
        {
            var s = await discord.LoginAsync(_discordCts.Token);
            await entitlements.RefreshAsync();
            await OnActivatedAsync();
            toasts.Show($"Discord подключён: {s.DisplayName}. Тариф: {TierNames.Display(entitlements.Tier)} 🐸", ToastKind.Success, 6);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is DiscordAuthException or System.Net.Http.HttpRequestException)
        {
            toasts.Show(ex.Message, ToastKind.Error, 8);
        }
        finally
        {
            DiscordWaiting = false;
        }
    }

    [RelayCommand]
    private void CancelDiscord() => _discordCts?.Cancel();

    [RelayCommand]
    private async Task LogoutDiscordAsync()
    {
        discord.Logout();
        await OnActivatedAsync();
    }

    partial void OnSelectedAccountChanged(GameAccount? value)
    {
        if (value is not null && accounts.Load().SelectedId != value.Id) accounts.Select(value.Id);
    }

    [RelayCommand]
    private async Task AddOfflineAsync()
    {
        try
        {
            accounts.AddOffline(OfflineNick);
            OfflineNick = "";
            await OnActivatedAsync();
            toasts.Show("Оффлайн-аккаунт добавлен", ToastKind.Success);
        }
        catch (GameAuthException ex)
        {
            toasts.Show(ex.Message, ToastKind.Warning);
        }
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        _msCts?.Cancel();
        _msCts = new CancellationTokenSource();
        try
        {
            DeviceCode = await accounts.BeginMicrosoftLoginAsync(_msCts.Token);
            MsWaiting = true;
            Clipboard.SetText(DeviceCode.UserCode);
            Shell.Open(DeviceCode.VerificationUri);
            var acc = await accounts.CompleteMicrosoftLoginAsync(DeviceCode, _msCts.Token);
            toasts.Show($"Вошли как {acc.Name} 🐸", ToastKind.Success);
            await OnActivatedAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show(ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            MsWaiting = false;
            DeviceCode = null;
        }
    }

    [RelayCommand]
    private void CancelMicrosoft() => _msCts?.Cancel();

    [RelayCommand]
    private void SelectAccount(GameAccount? acc)
    {
        if (acc is not null) SelectedAccount = acc;
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (DeviceCode is not null) Clipboard.SetText(DeviceCode.UserCode);
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(GameAccount? acc)
    {
        if (acc is null) return;
        accounts.Remove(acc.Id);
        await OnActivatedAsync();
    }

    /// <summary>Пароль берётся из PasswordBox во View (не храним его в свойствах VM).</summary>
    [RelayCommand]
    private async Task LauncherLoginAsync(object? passwordSource)
    {
        var password = (passwordSource as System.Windows.Controls.PasswordBox)?.Password ?? "";
        try
        {
            if (RegisterMode) await api.RegisterAsync(Login, Email, password, CancellationToken.None);
            else await api.LoginAsync(Login, password, CancellationToken.None);
            if (passwordSource is System.Windows.Controls.PasswordBox pb) pb.Clear();
            await entitlements.RefreshAsync();
            await OnActivatedAsync();
            toasts.Show("Аккаунт FrogLauncher подключён", ToastKind.Success);
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException)
        {
            toasts.Show(ex is ApiException ? ex.Message : "Сервер недоступен", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task LauncherLogoutAsync()
    {
        await api.LogoutAsync(CancellationToken.None);
        await OnActivatedAsync();
    }
}

/// <summary>«Настройки»: темы (часть — по подписке), RAM, Discord, поведение.</summary>
public partial class SettingsViewModel(
    SettingsService settings,
    ThemeService themes,
    EntitlementService entitlements,
    DiscordPresenceService presence,
    ToastService toasts) : ObservableObject, IPageActivated
{
    public ObservableCollection<ThemeItem> Themes { get; } = new();

    public LauncherSettings Settings => settings.Current;

    [ObservableProperty] private bool _discordPresence;
    [ObservableProperty] private bool _closeOnLaunch;
    [ObservableProperty] private int _defaultRamMb;

    public Task OnActivatedAsync()
    {
        Themes.ReplaceWith(ThemeService.Themes.Select(t => new ThemeItem(t, themes.IsUnlocked(t), t.Key == settings.Current.Theme)));
        DiscordPresence = settings.Current.DiscordPresence;
        CloseOnLaunch = settings.Current.CloseOnLaunch;
        DefaultRamMb = settings.Current.DefaultRamMb;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyThemeAsync(ThemeItem item)
    {
        if (!item.Unlocked)
        {
            toasts.Show($"Тема «{item.Theme.Name}» доступна с {item.Theme.RequiresLabel}", ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));
            return;
        }

        themes.Apply(item.Theme.Key);
        settings.Current.Theme = item.Theme.Key;
        settings.Save();
        await OnActivatedAsync();
    }

    partial void OnDiscordPresenceChanged(bool value)
    {
        settings.Current.DiscordPresence = value;
        settings.Save();
        presence.Enabled = value;
        _ = value ? presence.SetIdleAsync() : presence.ClearAsync();
    }

    partial void OnCloseOnLaunchChanged(bool value)
    {
        settings.Current.CloseOnLaunch = value;
        settings.Save();
    }

    partial void OnDefaultRamMbChanged(int value)
    {
        settings.Current.DefaultRamMb = value;
        settings.Save();
    }

    [RelayCommand]
    private static void OpenDataFolder() => Shell.Open(LauncherPaths.Root);

    public bool HasEntitlement(string key) => entitlements.IsAllowed(key);
}

public sealed record ThemeItem(ThemeInfo Theme, bool Unlocked, bool IsActive);

/// <summary>«Помощь»: о лаунчере, исходный код, юридические оговорки.</summary>
public partial class AboutViewModel(IOptions<LauncherOptions> options) : ObservableObject
{
    public string Version => LaunchService.LauncherVersion;
    public string GitHubUrl => options.Value.GitHubUrl;

    [RelayCommand]
    private void OpenGitHub() => Shell.Open(GitHubUrl);

    [RelayCommand]
    private static void OpenLogs() => Shell.Open(LauncherPaths.Logs);
}
