using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;
using FrogLauncher.Views;
using Microsoft.Win32;

namespace FrogLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ----- сервисы -----
    private readonly SettingsService _settings = new();
    private readonly AuthService _auth = new();
    private readonly VersionService _versions = new();
    private readonly JavaService _java = new();
    private readonly ModLoaderService _loaders = new();
    private readonly LaunchService _launch = new();
    private readonly InstanceService _instances = new();
    private readonly InfoService _info = new();
    private readonly SkinService _skins = new();
    private readonly ModpackService _packs = new();
    private readonly ModService _modSvc;
    private CancellationTokenSource? _cts;
    private List<JavaInfo> _javaAll = new();
    private List<McVersion> _allVersions = new();

    public MainViewModel()
    {
        _modSvc = new ModService(() => EffCfKey);
        NavCommand = new RelayCommand(p => SelectedTab = int.Parse((string)p!));
        PlayCommand = new AsyncRelayCommand(_ => PlayAsync(), _ => !Busy && !IsRunning);
        StopCommand = new RelayCommand(_ => _launch.Stop());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel());
        LoginOfflineCommand = new RelayCommand(_ => LoginOffline());
        LoginMsCommand = new AsyncRelayCommand(_ => LoginMicrosoftAsync());
        LogoutCommand = new RelayCommand(_ => Logout());
        RefreshVersionsCommand = new AsyncRelayCommand(_ => LoadVersionsAsync(true));
        InstallCommand = new AsyncRelayCommand(_ => InstallAsync(), _ => !Busy);
        NewInstanceCommand = new AsyncRelayCommand(_ => NewInstanceAsync());
        AddModsCommand = new RelayCommand(_ => AddMods());
        DeleteModCommand = new RelayCommand(_ => DeleteMod());
        RefreshModsCommand = new RelayCommand(_ => ReloadMods());
        BrowseJavaCommand = new RelayCommand(_ => BrowseJava());
        OpenClientGuideCommand = new RelayCommand(_ => OpenUrl(ClientGuideUrl));
        SaveInstanceCommand = new RelayCommand(_ => SaveInstance());
        DeleteInstanceCommand = new RelayCommand(_ => DeleteInstance());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(false));
        OpenModsCommand = new RelayCommand(_ => OpenFolder(true));
        ExportCommand = new RelayCommand(_ => ExportInstance());
        ImportCommand = new AsyncRelayCommand(_ => ImportAsync());
        SearchModsCommand = new AsyncRelayCommand(_ => SearchModsAsync(true));
        MoreModsCommand = new AsyncRelayCommand(_ => SearchModsAsync(false));
        InstallModCommand = new AsyncRelayCommand(_ => InstallModAsync(), _ => !Busy);
        OpenCurseForgeConsoleCommand = new RelayCommand(_ => OpenUrl(ApiConfig.CurseForgeConsole));
        RescanJavaCommand = new AsyncRelayCommand(_ => RescanJavaAsync());
        DownloadJavaCommand = new AsyncRelayCommand(_ => DownloadJavaAsync(), _ => !Busy);
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        CheckUpdatesCommand = new AsyncRelayCommand(_ => CheckUpdatesAsync());
        PickSkinCommand = new RelayCommand(_ => PickSkin());
        UploadSkinCommand = new AsyncRelayCommand(_ => UploadSkinAsync());
    }

    // ----- общие -----
    private int _tab; public int SelectedTab { get => _tab; set => Set(ref _tab, value); }
    private bool _busy; public bool Busy { get => _busy; set { Set(ref _busy, value); OnChanged(nameof(NotBusy)); } }
    public bool NotBusy => !Busy;
    private bool _initializing = true; public bool IsInitializing { get => _initializing; set { Set(ref _initializing, value); OnChanged(nameof(NeedLogin)); } }
    /// <summary>Первый запуск: пока нет аккаунта, показываем экран входа.</summary>
    public bool NeedLogin => !_initializing && _account == null;
    private bool _running; public bool IsRunning { get => _running; set { Set(ref _running, value); OnChanged(nameof(CanPlay)); } }
    public bool CanPlay => !IsRunning;
    private string _status = ""; public string StatusText { get => _status; set => Set(ref _status, value); }
    private double _progress; public double ProgressValue { get => _progress; set => Set(ref _progress, value); }
    private string _progressText = ""; public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public ICommand NavCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }

    // ----- аккаунт -----
    private Account? _account;
    public Account? CurrentAccount
    {
        get => _account;
        set { Set(ref _account, value); OnChanged(nameof(NeedLogin)); OnChanged(nameof(AccountName)); OnChanged(nameof(AccountTypeText)); OnChanged(nameof(IsLoggedIn)); OnChanged(nameof(IsLoggedOut)); }
    }
    public bool IsLoggedIn => _account != null;
    public bool IsLoggedOut => _account == null;
    public string AccountName => _account?.Username ?? Loc.T("not_logged");
    public string AccountTypeText => _account == null ? "" : _account.IsMicrosoft ? "Microsoft" : "Offline";
    private string _nick = ""; public string OfflineNick { get => _nick; set => Set(ref _nick, value); }
    private string _deviceMsg = ""; public string DeviceMessage { get => _deviceMsg; set => Set(ref _deviceMsg, value); }

    public ICommand LoginOfflineCommand { get; }
    public ICommand LoginMsCommand { get; }
    public ICommand LogoutCommand { get; }

    // ----- инстансы -----
    public ObservableCollection<Instance> Instances { get; } = new();
    private Instance? _inst;
    public Instance? SelectedInstance
    {
        get => _inst;
        set
        {
            if (!Set(ref _inst, value)) return;
            _settings.Settings.LastInstanceId = value?.Id; _settings.Save();
            OnChanged(nameof(HasInstance)); OnChanged(nameof(ModTarget)); OnChanged(nameof(InstanceStatusText)); OnChanged(nameof(InstanceInstalled));
            ModResults.Clear();
            ReloadMods();
            RefreshJavaCompat();
        }
    }
    public bool HasInstance => _inst != null;
    // ----- менеджер модов -----
    public ObservableCollection<ModFile> Mods { get; } = new();
    private ModFile? _selMod; public ModFile? SelectedMod { get => _selMod; set => Set(ref _selMod, value); }
    public string ModsText => $"{Loc.T("mods")}: {Mods.Count(m => m.Enabled)} / {Mods.Count}";

    private string ModsDir => _inst == null ? "" : Path.Combine(_inst.GameDir, "mods");

    private void ReloadMods()
    {
        Mods.Clear();
        if (_inst != null && Directory.Exists(ModsDir))
        {
            foreach (var f in Directory.GetFiles(ModsDir).Where(f =>
                         f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var m = new ModFile(f);
                m.PropertyChanged += (_, _) => OnChanged(nameof(ModsText));
                Mods.Add(m);
            }
        }
        OnChanged(nameof(ModsText));
    }

    private void AddMods()
    {
        if (_inst == null) return;
        var dlg = new OpenFileDialog { Filter = "Моды (*.jar)|*.jar", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        Directory.CreateDirectory(ModsDir);
        foreach (var f in dlg.FileNames)
        {
            try { File.Copy(f, Path.Combine(ModsDir, Path.GetFileName(f)), true); }
            catch (Exception ex) { Log.Error("Копирование мода", ex); Dialogs.Error($"Не удалось добавить {Path.GetFileName(f)}: {ex.Message}"); }
        }
        ReloadMods();
    }

    private void DeleteMod()
    {
        if (SelectedMod == null) return;
        if (MessageBox.Show($"Удалить мод «{SelectedMod.Name}»?", "FrogLauncher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(SelectedMod.FullPath);
            if (_inst != null) ModMetaStore.Remove(_inst, SelectedMod.Name);
        }
        catch (Exception ex) { Log.Error("Удаление мода", ex); Dialogs.Error(ex.Message); }
        ReloadMods();
    }

    private void BrowseJava()
    {
        if (_inst == null) return;
        var dlg = new OpenFileDialog { Filter = "java.exe|java.exe;javaw.exe" };
        if (dlg.ShowDialog() != true) return;
        _inst.JavaPath = dlg.FileName;
        OnChanged(nameof(SelectedInstance)); // обновить поле в интерфейсе
    }

    public ICommand NewInstanceCommand { get; }
    public ICommand AddModsCommand { get; }
    public ICommand DeleteModCommand { get; }
    public ICommand RefreshModsCommand { get; }
    public ICommand BrowseJavaCommand { get; }
    public ICommand SaveInstanceCommand { get; }
    public ICommand DeleteInstanceCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }

    // ----- версии -----
    public ObservableCollection<McVersion> FilteredVersions { get; } = new();
    private int _filter; public int VersionFilter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    private McVersion? _selVer;
    public McVersion? SelectedVersion
    {
        get => _selVer;
        set { if (Set(ref _selVer, value)) { _ = OnVersionSelectedAsync(); } }
    }
    public string[] LoaderTypes => ModLoaderService.Loaders;
    private string _loader = "Vanilla";
    public string SelectedLoader { get => _loader; set { if (Set(ref _loader, value)) { OnChanged(nameof(NeedsLoaderVersion)); _ = LoadLoaderVersionsAsync(); } } }
    public bool NeedsLoaderVersion => _loader != "Vanilla";
    public ObservableCollection<string> LoaderVersions { get; } = new();
    private string? _loaderVer; public string? SelectedLoaderVersion { get => _loaderVer; set => Set(ref _loaderVer, value); }

    public ICommand RefreshVersionsCommand { get; }
    public ICommand InstallCommand { get; }

    // ----- Java -----
    public ObservableCollection<JavaInfo> JavaList { get; } = new();
    public int[] JavaMajors { get; } = { 8, 17, 21 };
    private int _dlJava = 17; public int DownloadJavaMajor { get => _dlJava; set => Set(ref _dlJava, value); }
    private string _javaReq = ""; public string JavaRequirementText { get => _javaReq; set => Set(ref _javaReq, value); }
    public ICommand RescanJavaCommand { get; }
    public ICommand DownloadJavaCommand { get; }

    // ----- настройки -----
    public string[] Languages { get; } = { "ru", "en" };
    public string[] Themes { get; } = { "Green", "Cyan", "Purple" };
    private string _lang = "ru"; public string Language { get => _lang; set => Set(ref _lang, value); }
    private string _theme = "Green"; public string Theme { get => _theme; set => Set(ref _theme, value); }
    private string _root = ""; public string GameRoot { get => _root; set => Set(ref _root, value); }
    private string _proxy = ""; public string Proxy { get => _proxy; set => Set(ref _proxy, value); }
    private string _repo = ""; public string UpdateRepo { get => _repo; set => Set(ref _repo, value); }
    private string _clientId = ""; public string MsClientId { get => _clientId; set => Set(ref _clientId, value); }
    private string _cfKey = ""; public string CurseForgeKey { get => _cfKey; set => Set(ref _cfKey, value); }
    public ICommand OpenCurseForgeConsoleCommand { get; }

    // «эффективные» значения: из настроек, а если пусто — вшитые при сборке
    private string EffCfKey => !string.IsNullOrWhiteSpace(CurseForgeKey) ? CurseForgeKey.Trim() : BuiltIn.CurseForgeKey;
    private string EffMsClientId => !string.IsNullOrWhiteSpace(MsClientId) ? MsClientId.Trim() : BuiltIn.MsClientId;
    private string EffUpdateRepo => !string.IsNullOrWhiteSpace(UpdateRepo) ? UpdateRepo.Trim() : BuiltIn.UpdateRepo;
    private string _skinPath = "";
    public string SkinPath { get => _skinPath; set { Set(ref _skinPath, value); OnChanged(nameof(HasSkin)); SkinPreview = HasSkin ? SkinRenderer.Render(value) : null; } }
    private ImageSource? _skinPreview; public ImageSource? SkinPreview { get => _skinPreview; set => Set(ref _skinPreview, value); }
    private const string ClientGuideUrl = "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade/isMSAApp~/true";
    public ICommand OpenClientGuideCommand { get; }
    public bool HasSkin => !string.IsNullOrEmpty(_skinPath) && File.Exists(_skinPath);
    private bool _slim; public bool SkinSlim { get => _slim; set => Set(ref _slim, value); }
    public string AppVersion => "v" + ApiConfig.LauncherVersion;
    public ICommand SaveSettingsCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand PickSkinCommand { get; }
    public ICommand UploadSkinCommand { get; }

    // ----- новости/статус/лог -----
    public ObservableCollection<NewsItem> News { get; } = new();
    public ObservableCollection<ServerStatus> ServerStatuses { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // =====================================================================
    //  Инициализация
    // =====================================================================
    public async Task InitAsync()
    {
        _settings.Load();
        var s = _settings.Settings;
        Language = s.Language; Theme = s.Theme; GameRoot = s.GameRoot ?? ""; Proxy = s.Proxy ?? "";
        UpdateRepo = s.UpdateRepo; MsClientId = s.MicrosoftClientId; CurseForgeKey = s.CurseForgeApiKey;
        NewsUrlOverride = s.NewsUrl; HideOnLaunch = s.HideOnLaunch; CloseInsteadOfHide = s.CloseInsteadOfHide;
        ReopenAfterExit = s.ReopenAfterExit; ShowGameLog = s.ShowGameLog; AutoStart = s.AutoStart; DefaultMaxRam = s.DefaultMaxRamMb;
        Loc.I.Language = s.Language; App.ApplyTheme(s.Theme); Http.Configure(s.Proxy);
        AppPaths.EnsureAll();

        CurrentAccount = _settings.LoadAccount();
        if (CurrentAccount != null) OfflineNick = CurrentAccount.Username;
        ReloadInstances();
        InitExtras();
        StatusText = Loc.T("ready");

        _ = LoadNewsAndStatusAsync();
        _ = RescanJavaAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await LoadVersionsAsync(false, cts.Token);
        }
        catch (Exception ex) { Log.Error("Манифест версий", ex); StatusText = "Не удалось загрузить список версий (кнопка «Обновить» на вкладке «Версии»): " + ex.Message; }
        finally { IsInitializing = false; }
        AutoLaunchIfRequested();
    }

    private async Task LoadNewsAndStatusAsync()
    {
        await RefreshLauncherNewsAsync();
        try
        {
            var st = await _info.GetStatusAsync(CancellationToken.None);
            ServerStatuses.Clear(); foreach (var x in st) ServerStatuses.Add(x);
        }
        catch (Exception ex) { Log.Warn("Статус: " + ex.Message); }
    }

    private void ReloadInstances()
    {
        Instances.Clear();
        foreach (var i in _instances.LoadAll()) Instances.Add(i);
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == _settings.Settings.LastInstanceId) ?? Instances.FirstOrDefault();
    }

    // =====================================================================
    //  Аккаунт
    // =====================================================================
    private void LoginOffline()
    {
        try
        {
            CurrentAccount = _auth.CreateOffline(OfflineNick);
            _settings.SaveAccount(CurrentAccount);
            DeviceMessage = "";
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    private async Task LoginMicrosoftAsync()
    {
        if (string.IsNullOrWhiteSpace(EffMsClientId))
        {
            var ans = MessageBox.Show("В этой сборке лаунчера не вшит Microsoft Client ID (его один раз добавляет сборщик в keys.props, см. README).\n\n" +
                "Можно вписать свой ID в Настройки → Дополнительно. Открыть страницу регистрации?", "FrogLauncher", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes) { OpenUrl(ClientGuideUrl); SelectedTab = 4; }
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var dc = await _auth.StartDeviceFlowAsync(EffMsClientId, cts.Token);
        DeviceMessage = $"Откройте {dc.VerificationUri} и введите код: {dc.UserCode}";
        try { Clipboard.SetText(dc.UserCode); } catch { }
        try { Process.Start(new ProcessStartInfo(dc.VerificationUri) { UseShellExecute = true }); } catch { }
        try
        {
            CurrentAccount = await _auth.CompleteDeviceFlowAsync(EffMsClientId, dc, cts.Token);
            _settings.SaveAccount(CurrentAccount);
        }
        finally { DeviceMessage = ""; }
    }

    private void Logout() { CurrentAccount = null; _settings.SaveAccount(null); }

    // =====================================================================
    //  Версии
    // =====================================================================
    private async Task LoadVersionsAsync(bool force, CancellationToken ct = default)
    {
        StatusText = "Загрузка списка версий…";
        _allVersions = await _versions.GetVersionsAsync(force, ct);
        ApplyFilter();
        OnChanged(nameof(AllVersionIds));
        StatusText = Loc.T("ready");
    }

    private void ApplyFilter()
    {
        var kind = (VersionKind)_filter;
        FilteredVersions.Clear();
        foreach (var v in _allVersions.Where(v => v.Kind == kind)) FilteredVersions.Add(v);
    }

    private async Task OnVersionSelectedAsync()
    {
        var v = _selVer;
        if (v == null) return;
        if (v.SizeText == "—")
        {
            try { var size = await _versions.GetClientSizeAsync(v, CancellationToken.None); if (size > 0) v.SizeText = $"{size / 1024.0 / 1024.0:F1} МБ"; }
            catch (Exception ex) { Log.Warn("Размер версии: " + ex.Message); }
        }
        await LoadLoaderVersionsAsync();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        LoaderVersions.Clear(); SelectedLoaderVersion = null;
        if (_loader == "Vanilla" || _selVer == null) return;
        try
        {
            var list = await _loaders.GetLoaderVersionsAsync(_loader, _selVer.Id, CancellationToken.None);
            foreach (var x in list) LoaderVersions.Add(x);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            if (list.Count == 0) StatusText = $"{_loader}: нет версий загрузчика для Minecraft {_selVer.Id}";
        }
        catch (Exception ex) { Log.Warn("Версии загрузчика: " + ex.Message); StatusText = "Не удалось получить версии загрузчика: " + ex.Message; }
    }

    private Task InstallAsync()
    {
        var v = SelectedVersion ?? throw new InvalidOperationException(Loc.T("select_version"));
        if (_loader != "Vanilla" && string.IsNullOrEmpty(SelectedLoaderVersion))
            throw new InvalidOperationException("Выберите версию загрузчика.");
        return CreateInstanceAsync(null, v.Id, _loader, SelectedLoaderVersion);
    }

    /// <summary>Ставит версию (+модлоадер) и создаёт инстанс. Используется вкладкой «Версии» и диалогом «Новый инстанс».</summary>
    public async Task CreateInstanceAsync(string? name, string mcId, string loader, string? loaderVersion)
    {
        await RunBusyAsync(async (ct, prog) =>
        {
            var inst = await InstallInstanceCoreAsync(name, mcId, loader, loaderVersion, prog, ct);
            StatusText = $"Установлено: {inst.Display}";
            SelectedTab = 0;
        });
    }

    /// <summary>Установка MC + модлоадера и создание инстанса (без собственного «занято» — вызывается из RunBusyAsync).</summary>
    private async Task<Instance> InstallInstanceCoreAsync(string? name, string mcId, string loader, string? loaderVersion,
        IProgress<DownloadProgress> prog, CancellationToken ct)
    {
        StatusText = $"Установка Minecraft {mcId}…";
        await _versions.InstallAsync(mcId, prog, ct);

        string launchId = mcId;
        if (loader != "Vanilla")
        {
            JavaInfo? java = null;
            if (loader is "Forge" or "NeoForge")
                java = await EnsureJavaAsync(mcId, null, prog, ct);
            StatusText = $"Установка {loader} {loaderVersion}…";
            launchId = await _loaders.InstallAsync(loader, mcId, loaderVersion!, java, prog, ct);
            StatusText = "Загрузка библиотек загрузчика…";
            await _versions.InstallAsync(launchId, prog, ct);
        }

        var inst = new Instance
        {
            Name = string.IsNullOrWhiteSpace(name) ? (loader == "Vanilla" ? $"Minecraft {mcId}" : $"{mcId} {loader}") : name.Trim(),
            McVersion = mcId, LaunchVersionId = launchId, Loader = loader, LoaderVersion = loaderVersion ?? "",
            MaxRamMb = _settings.Settings.DefaultMaxRamMb
        };
        _instances.Save(inst);
        Instances.Add(inst);
        SelectedInstance = inst;
        return inst;
    }

    /// <summary>Подбирает версию загрузчика из списка по значению из сборки (47.2.0 → 1.20.1-47.2.0).</summary>
    private async Task<string> ResolveLoaderVersionAsync(string loader, string mc, string? raw, CancellationToken ct)
    {
        var list = await _loaders.GetLoaderVersionsAsync(loader, mc, ct);
        if (list.Count == 0) throw new InvalidOperationException($"{loader}: нет версий для Minecraft {mc}.");
        if (raw != null)
        {
            var m = list.FirstOrDefault(x => x == raw || x == mc + "-" + raw || x.StartsWith(mc + "-" + raw + "-") || x.EndsWith("-" + raw));
            if (m != null) return m;
            Log.Warn($"Версия загрузчика {loader} {raw} не найдена, беру последнюю: {list[0]}");
        }
        return list[0];
    }

    public IReadOnlyList<string> AllVersionIds => _allVersions.Select(v => v.Id).ToList();

    public Task<List<string>> GetLoaderVersionsAsync(string loader, string mcId) =>
        _loaders.GetLoaderVersionsAsync(loader, mcId, CancellationToken.None);

    // =====================================================================
    //  Инстансы
    // =====================================================================
    private async Task NewInstanceAsync()
    {
        if (_allVersions.Count == 0)
        {
            try { await LoadVersionsAsync(true); }
            catch (Exception ex) { throw new InvalidOperationException("Не удалось получить список версий: " + ex.Message); }
        }
        var w = new NewInstanceWindow(this) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true)
            await CreateInstanceAsync(w.InstanceName, w.McId, w.Loader, w.LoaderVersion);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Открытие ссылки: " + ex.Message); }
    }

    private void SaveInstance()
    {
        if (SelectedInstance == null) return;
        var i = SelectedInstance;
        if (i.MaxRamMb < i.MinRamMb) i.MaxRamMb = i.MinRamMb;
        _instances.Save(i);
        var idx = Instances.IndexOf(i);
        Instances[idx] = i;               // обновить карточку
        SelectedInstance = i;
        StatusText = "Сохранено";
    }

    private void DeleteInstance()
    {
        if (SelectedInstance == null) return;
        if (MessageBox.Show($"Удалить версию «{SelectedInstance.Name}» вместе с его папкой (миры, моды)?", "FrogLauncher",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var i = SelectedInstance;
        _instances.Delete(i);
        Instances.Remove(i);
        SelectedInstance = Instances.FirstOrDefault();
    }

    private void OpenFolder(bool mods)
    {
        if (SelectedInstance == null) return;
        var dir = mods ? Path.Combine(SelectedInstance.GameDir, "mods") : SelectedInstance.GameDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void ExportInstance()
    {
        if (SelectedInstance == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Сборка Modrinth (*.mrpack)|*.mrpack|Сборка CurseForge (*.zip)|*.zip|Версия FrogLauncher (*.zip)|*.zip",
            FilterIndex = 1, FileName = SelectedInstance.Name
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            switch (dlg.FilterIndex)
            {
                case 1: _packs.Export(SelectedInstance, dlg.FileName, PackFormat.Mrpack); break;
                case 2: _packs.Export(SelectedInstance, dlg.FileName, PackFormat.CurseForge); break;
                default: _instances.Export(SelectedInstance, dlg.FileName); break;
            }
            StatusText = "Экспортировано: " + dlg.FileName;
        }
        catch (Exception ex) { Log.Error("Экспорт", ex); Dialogs.Error("Не удалось экспортировать: " + ex.Message); }
    }

    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Сборки и версии (*.mrpack;*.zip)|*.mrpack;*.zip" };
        if (dlg.ShowDialog() != true) return;

        var pack = _packs.Inspect(dlg.FileName);
        if (pack.Format == PackFormat.Native)
        {
            var inst = _instances.Import(dlg.FileName);
            Instances.Add(inst); SelectedInstance = inst;
            StatusText = $"Импортировано: {inst.Name}. Если версия не установлена — поставьте её на вкладке «Версии».";
            return;
        }
        if (pack.Format == PackFormat.CurseForge && string.IsNullOrWhiteSpace(EffCfKey))
            throw new InvalidOperationException("Для импорта сборки CurseForge нужен бесплатный API-ключ: Настройки → «CurseForge API key».");

        List<string> failed = new();
        await RunBusyAsync(async (ct, prog) =>
        {
            string? loaderVer = null;
            if (pack.Loader != "Vanilla") loaderVer = await ResolveLoaderVersionAsync(pack.Loader, pack.McVersion, pack.LoaderRaw, ct);
            var inst = await InstallInstanceCoreAsync(pack.Name, pack.McVersion, pack.Loader, loaderVer, prog, ct);

            StatusText = $"Скачивание файлов сборки «{pack.Name}»…";
            failed = await _packs.ApplyAsync(pack, inst, _modSvc.CurseForge, prog, ct);
            ReloadMods();
            StatusText = $"Сборка импортирована: {inst.Display}";
            SelectedTab = 0;
        });
        if (failed.Count > 0)
            Dialogs.Error("Не удалось скачать (автор запретил загрузку сторонним лаунчерам или файл удалён):\n\n" +
                          string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? $"\n… и ещё {failed.Count - 15}" : "") +
                          "\n\nСкачайте их вручную с сайта и положите в папку mods.");
    }

    // =====================================================================
    //  Каталог модов (Modrinth / CurseForge)
    // =====================================================================
    public string[] ModSources => ModService.Sources;
    private string _modSource = "Modrinth"; public string SelectedModSource { get => _modSource; set { if (Set(ref _modSource, value)) { ModResults.Clear(); } } }
    private string _modQuery = ""; public string ModQuery { get => _modQuery; set => Set(ref _modQuery, value); }
    public ObservableCollection<ModProject> ModResults { get; } = new();
    private ModProject? _selModResult; public ModProject? SelectedModResult { get => _selModResult; set => Set(ref _selModResult, value); }
    private int _modOffset;
    private bool _modHasMore; public bool ModHasMore { get => _modHasMore; set => Set(ref _modHasMore, value); }
    private string _modStatus = ""; public string ModStatus { get => _modStatus; set => Set(ref _modStatus, value); }
    public string ModTarget => _inst == null ? "" : $"{_inst.Name}  •  {_inst.McVersion}  •  {_inst.Loader}";

    public ICommand SearchModsCommand { get; }
    public ICommand MoreModsCommand { get; }
    public ICommand InstallModCommand { get; }

    private async Task SearchModsAsync(bool fresh)
    {
        var inst = _inst ?? throw new InvalidOperationException(Loc.T("pick_instance"));
        if (fresh) { _modOffset = 0; ModResults.Clear(); }
        ModStatus = "Поиск…";
        var list = await _modSvc.SearchAsync(_modSource, _modQuery, inst, _modOffset, CancellationToken.None);
        foreach (var m in list) ModResults.Add(m);
        _modOffset += list.Count;
        ModHasMore = list.Count >= 20;
        ModStatus = ModResults.Count == 0 ? "Ничего не найдено" : $"Найдено: {ModResults.Count}";
    }

    private async Task InstallModAsync()
    {
        var inst = _inst ?? throw new InvalidOperationException(Loc.T("pick_instance"));
        var mod = SelectedModResult ?? throw new InvalidOperationException("Выберите мод из списка.");
        await RunBusyAsync(async (ct, prog) =>
        {
            StatusText = $"Установка мода «{mod.Name}»…";
            var names = await _modSvc.InstallAsync(mod, inst, prog, ct);
            ReloadMods();
            ModStatus = "Установлено: " + string.Join(", ", names);
            StatusText = ModStatus;
        });
    }

    // =====================================================================
    //  Java
    // =====================================================================
    private async Task RescanJavaAsync()
    {
        _javaAll = await _java.DetectAsync();
        RefreshJavaCompat();
    }

    private void RefreshJavaCompat()
    {
        int required = 17; string mc = "";
        if (_inst != null)
        {
            mc = _inst.McVersion;
            int? fromJson = null;
            try { if (VersionJson.Exists(_inst.LaunchVersionId)) fromJson = VersionJson.JavaMajor(VersionJson.LoadMerged(_inst.LaunchVersionId)); } catch { }
            required = JavaService.RequiredMajor(mc, fromJson);
            JavaRequirementText = $"{_inst.Name}: Java {required}+";
        }
        else JavaRequirementText = "";
        JavaService.MarkCompat(_javaAll, required);
        JavaList.Clear(); foreach (var j in _javaAll) JavaList.Add(j);
    }

    private async Task DownloadJavaAsync()
    {
        await RunBusyAsync(async (ct, prog) =>
        {
            StatusText = $"Скачивание Java {DownloadJavaMajor}…";
            await _java.DownloadAsync(DownloadJavaMajor, prog, ct);
            await RescanJavaAsync();
            StatusText = $"Java {DownloadJavaMajor} установлена";
        });
    }

    /// <summary>Подбирает подходящую Java; если нет — скачивает автоматически.</summary>
    private async Task<JavaInfo> EnsureJavaAsync(string versionId, string? manual, IProgress<DownloadProgress> prog, CancellationToken ct)
    {
        int? fromJson = null;
        string mc = versionId;
        try { var m = VersionJson.LoadMerged(versionId); fromJson = VersionJson.JavaMajor(m); mc = (string?)m["_jarId"] ?? versionId; } catch { }
        int required = JavaService.RequiredMajor(mc, fromJson);

        if (_javaAll.Count == 0) _javaAll = await _java.DetectAsync();
        var pick = _java.Pick(_javaAll, required, manual);
        if (pick != null) return pick;

        StatusText = $"Нужна Java {required} — скачиваю автоматически…";
        var info = await _java.DownloadAsync(required, prog, ct);
        _javaAll = await _java.DetectAsync();
        RefreshJavaCompat();
        return info;
    }

    // =====================================================================
    //  Запуск
    // =====================================================================
    private async Task PlayAsync()
    {
        var inst = SelectedInstance ?? throw new InvalidOperationException(Loc.T("pick_instance"));
        if (CurrentAccount == null) throw new InvalidOperationException("Сначала войдите: оффлайн-ник или аккаунт Microsoft.");
        LogLines.Clear();

        JavaInfo? java = null;
        await RunBusyAsync(async (ct, prog) =>
        {
            // обновить токен Microsoft при необходимости
            if (CurrentAccount.IsMicrosoft && DateTime.UtcNow >= CurrentAccount.ExpiresAt)
            {
                StatusText = "Обновление сессии Microsoft…";
                CurrentAccount = await _auth.RefreshAsync(CurrentAccount, EffMsClientId, ct);
                _settings.SaveAccount(CurrentAccount);
            }
            StatusText = "Проверка файлов игры…";
            await _versions.InstallAsync(inst.LaunchVersionId, prog, ct);
            java = await EnsureJavaAsync(inst.LaunchVersionId, inst.JavaPath, prog, ct);
            if (!string.IsNullOrWhiteSpace(inst.PreLaunchCommand))
            {
                StatusText = "Команда перед запуском…";
                await RunCommandAsync(inst.PreLaunchCommand, inst.GameDir, wait: true);
            }
        });
        if (java == null) return;
        OnChanged(nameof(InstanceStatusText));

        IsRunning = true;
        StatusText = Loc.T("running");
        ProgressValue = 0; ProgressText = "";
        var started = DateTime.Now;
        var s = _settings.Settings;
        bool hidden = false, closed = false;
        try
        {
            var res = await _launch.RunAsync(inst, CurrentAccount, java, line =>
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    LogLines.Add(line);
                    while (LogLines.Count > 200) LogLines.RemoveAt(0);
                }),
                pid =>
                {
                    // после старта игры: скрыть или закрыть лаунчер (по настройкам)
                    if (!s.HideOnLaunch) return;
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (s.CloseInsteadOfHide)
                        {
                            if (s.ReopenAfterExit) StartRelaunchHelper(pid);
                            closed = true;
                            App.Quitting = true;
                            Application.Current.Shutdown();
                        }
                        else { Application.Current.MainWindow?.Hide(); hidden = true; }
                    });
                });

            // учёт времени игры
            inst.PlaySeconds += (long)(DateTime.Now - started).TotalSeconds;
            inst.LastPlayed = DateTime.Now;
            try { _instances.Save(inst); } catch (Exception ex) { Log.Warn("Время игры: " + ex.Message); }
            OnChanged(nameof(InstanceStatusText));
            if (!string.IsNullOrWhiteSpace(inst.PostExitCommand)) _ = RunCommandAsync(inst.PostExitCommand, inst.GameDir, wait: false);
            if (closed) return;

            StatusText = res.StoppedByUser ? "Игра остановлена" : res.Crashed ? "Игра завершилась с ошибкой" : Loc.T("ready");

            // игра закрыта → возвращаем лаунчер
            if (hidden)
            {
                if (s.ReopenAfterExit) RestoreWindow();
                else { App.Quitting = true; Application.Current.Shutdown(); return; }
            }
            if (res.Crashed) new CrashWindow(res.LastLines, res.MclogsUrl) { Owner = Application.Current.MainWindow }.Show();
        }
        finally { IsRunning = false; ReloadMods(); }
    }

    // =====================================================================
    //  Настройки, скин, обновления
    // =====================================================================
    private void SaveSettings()
    {
        var s = _settings.Settings;
        s.Language = Language; s.Theme = Theme; s.GameRoot = string.IsNullOrWhiteSpace(GameRoot) ? null : GameRoot.Trim();
        s.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim(); s.UpdateRepo = UpdateRepo.Trim(); s.MicrosoftClientId = MsClientId.Trim(); s.CurseForgeApiKey = CurseForgeKey.Trim();
        s.NewsUrl = NewsUrlOverride.Trim(); s.HideOnLaunch = HideOnLaunch; s.CloseInsteadOfHide = CloseInsteadOfHide;
        s.ReopenAfterExit = ReopenAfterExit; s.ShowGameLog = ShowGameLog; s.AutoStart = AutoStart; s.DefaultMaxRamMb = Math.Max(1024, DefaultMaxRam);
        try { Autostart.Set(s.AutoStart); } catch (Exception ex) { Log.Warn("Автозапуск: " + ex.Message); }
        _ = RefreshLauncherNewsAsync();
        _settings.Save();

        Loc.I.Language = Language; App.ApplyTheme(Theme); Http.Configure(s.Proxy);
        OnChanged(nameof(AccountName));
        var oldRoot = AppPaths.GameRoot;
        AppPaths.GameRoot = s.GameRoot ?? "";
        if (!string.Equals(oldRoot, AppPaths.GameRoot, StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.EnsureAll(); ReloadInstances();
        }
        StatusText = "Настройки сохранены";
    }

    private async Task CheckUpdatesAsync()
    {
        var (newer, tag, url) = await _info.CheckUpdateAsync(EffUpdateRepo, CancellationToken.None);
        if (!newer) { Dialogs.Info($"У вас последняя версия ({ApiConfig.LauncherVersion})."); return; }
        if (MessageBox.Show($"Доступна версия {tag}. Открыть страницу загрузки?", "FrogLauncher", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void PickSkin()
    {
        var dlg = new OpenFileDialog { Filter = "PNG|*.png" };
        if (dlg.ShowDialog() == true) SkinPath = dlg.FileName;
    }

    private async Task UploadSkinAsync()
    {
        if (CurrentAccount == null) throw new InvalidOperationException("Сначала войдите в аккаунт Microsoft.");
        if (!HasSkin) throw new InvalidOperationException("Выберите PNG-файл скина.");
        await _skins.UploadAsync(CurrentAccount, SkinPath, SkinSlim, CancellationToken.None);
        Dialogs.Info("Скин загружен!");
    }

    // =====================================================================
    //  Помощник «занято + прогресс»
    // =====================================================================
    private async Task RunBusyAsync(Func<CancellationToken, IProgress<DownloadProgress>, Task> work)
    {
        _cts = new CancellationTokenSource();
        Busy = true; ProgressValue = 0; ProgressText = "";
        var prog = new Progress<DownloadProgress>(p =>
        {
            ProgressValue = p.Percent;
            ProgressText = (string.IsNullOrEmpty(p.Stage) ? "" : p.Stage + "  •  ") + Downloader.FormatProgress(p);
        });
        try { await work(_cts.Token, prog); }
        catch (OperationCanceledException) { StatusText = "Отменено"; }
        catch (Exception ex) { Log.Error("Операция не удалась", ex); StatusText = "Ошибка: " + ex.Message; Dialogs.Error(ex.Message); }
        finally { Busy = false; ProgressValue = 0; ProgressText = ""; _cts.Dispose(); _cts = null; }
    }
}
