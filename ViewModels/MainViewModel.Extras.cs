using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;

namespace FrogLauncher.ViewModels;

/// <summary>Дополнительные функции: новости лаунчера, поведение при запуске, ярлыки, бэкапы, обновление модов и т.д.</summary>
public sealed partial class MainViewModel
{
    // ----- настройки поведения -----
    private bool _hideOnLaunch = true; public bool HideOnLaunch { get => _hideOnLaunch; set => Set(ref _hideOnLaunch, value); }
    private bool _closeInstead; public bool CloseInsteadOfHide { get => _closeInstead; set => Set(ref _closeInstead, value); }
    private bool _reopen = true; public bool ReopenAfterExit { get => _reopen; set => Set(ref _reopen, value); }
    private bool _showLog = true; public bool ShowGameLog { get => _showLog; set => Set(ref _showLog, value); }
    private bool _autoStart; public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    private int _defRam = 4096; public int DefaultMaxRam { get => _defRam; set => Set(ref _defRam, value); }
    private string _newsUrl = ""; public string NewsUrlOverride { get => _newsUrl; set => Set(ref _newsUrl, value); }

    // ----- статус выбранной версии -----
    public bool InstanceInstalled
    {
        get
        {
            if (_inst == null) return false;
            try
            {
                if (!VersionJson.Exists(_inst.LaunchVersionId)) return false;
                var m = VersionJson.LoadMerged(_inst.LaunchVersionId);
                return File.Exists(VersionJson.JarPath((string)m["_jarId"]!));
            }
            catch { return false; }
        }
    }

    public string InstanceStatusText
    {
        get
        {
            if (_inst == null) return Loc.T("ver_none");
            if (!InstanceInstalled) return Loc.T("ver_missing");
            var loader = _inst.Loader == "Vanilla" ? "" : $" • {_inst.Loader} {ModpackService.CleanLoaderVersion(_inst)}";
            return $"{Loc.T("ver_installed")}: Minecraft {_inst.McVersion}{loader}   ⏱ {_inst.PlayTimeText}";
        }
    }

    // ----- команды -----
    public ICommand OpenGameFolderCommand { get; private set; } = null!;
    public ICommand OpenLogsCommand { get; private set; } = null!;
    public ICommand ClearCacheCommand { get; private set; } = null!;
    public ICommand ResetSettingsCommand { get; private set; } = null!;
    public ICommand RepairCommand { get; private set; } = null!;
    public ICommand AutoRamCommand { get; private set; } = null!;
    public ICommand CloneCommand { get; private set; } = null!;
    public ICommand BackupCommand { get; private set; } = null!;
    public ICommand DesktopShortcutCommand { get; private set; } = null!;
    public ICommand UpdateModsCommand { get; private set; } = null!;
    public ICommand RefreshNewsCommand { get; private set; } = null!;

    private bool _extrasReady;
    private DiscordRpc? _rpc;
    private CancellationTokenSource? _saveCts;
    private bool _loadingSettings;

    // «Дополнительные действия» в «Моих версиях» раскрываются кнопкой «Подробнее»
    private bool _showMore; public bool ShowMoreActions { get => _showMore; set => Set(ref _showMore, value); }
    public ICommand ToggleMoreCommand { get; private set; } = null!;
    public string AboutVersion => "v" + ApiConfig.LauncherVersion;

    private void InitExtras()
    {
        if (_extrasReady) return;
        _extrasReady = true;
        ToggleMoreCommand = new RelayCommand(_ => ShowMoreActions = !ShowMoreActions);
        OpenLoginCommand = new RelayCommand(_ => LoginOpen = true);
        CloseLoginCommand = new RelayCommand(_ => LoginOpen = false);
        OpenDiscordCommand = new RelayCommand(_ => OpenDiscord());
        OpenGithubCommand = new RelayCommand(_ => OpenUrl(ApiConfig.GithubRepoUrl));
        foreach (var n in new[] { nameof(ToggleMoreCommand), nameof(OpenLoginCommand), nameof(CloseLoginCommand), nameof(OpenDiscordCommand), nameof(OpenGithubCommand) })
            OnChanged(n);

        // Discord: «Играет в FrogLauncher» (нужен DiscordAppId в keys.props)
        InitPrivileges();
        _rpc = new DiscordRpc(BuiltIn.DiscordAppId);
        ApplyPerkRuntime();
        _rpc.Set("В лаунчере", "FrogLauncher", resetTimer: true);

        // переключатели сохраняются сразу (раньше — только по кнопке «Применить», поэтому после перезапуска всё сбрасывалось)
        PropertyChanged += (_, e) =>
        {
            if (_loadingSettings) return;
            if (e.PropertyName is nameof(HideOnLaunch) or nameof(CloseInsteadOfHide) or nameof(ReopenAfterExit) or nameof(ShowGameLog)
                or nameof(AutoStart) or nameof(Language) or nameof(Theme) or nameof(DefaultMaxRam)
                or nameof(AutoBackupWorlds) or nameof(AutoUpdateMods) or nameof(FastDownloads) or nameof(BetaUpdates) or nameof(CustomPresence))
                QueueAutoSave();
        };
        OpenGameFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.GameRoot));
        OpenLogsCommand = new RelayCommand(_ => OpenPath(AppPaths.Logs));
        ClearCacheCommand = new RelayCommand(_ => ClearCache());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        RepairCommand = new AsyncRelayCommand(_ => RepairAsync(), _ => !Busy);
        AutoRamCommand = new RelayCommand(_ => AutoRam());
        CloneCommand = new RelayCommand(_ => CloneInstance());
        BackupCommand = new RelayCommand(_ => BackupWorlds());
        DesktopShortcutCommand = new RelayCommand(_ => CreateDesktopShortcut());
        UpdateModsCommand = new AsyncRelayCommand(_ => UpdateModsAsync(), _ => !Busy);
        RefreshNewsCommand = new AsyncRelayCommand(_ => RefreshLauncherNewsAsync());
        foreach (var n in new[] { nameof(OpenGameFolderCommand), nameof(OpenLogsCommand), nameof(ClearCacheCommand), nameof(ResetSettingsCommand),
                     nameof(RepairCommand), nameof(AutoRamCommand), nameof(CloneCommand), nameof(BackupCommand), nameof(DesktopShortcutCommand),
                     nameof(UpdateModsCommand), nameof(RefreshNewsCommand) })
            OnChanged(n);

        // смена языка → перестроить новости и подписи
        Loc.I.PropertyChanged += (_, _) =>
        {
            RebuildNews();
            OnChanged(nameof(InstanceStatusText)); OnChanged(nameof(AccountName)); OnChanged(nameof(ModsText));
        };

        // новости лаунчера обновляются сами каждые 3 минуты
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(3));
                Application.Current.Dispatcher.BeginInvoke(new Action(() => { _ = RefreshLauncherNewsAsync(); _ = RefreshPrivilegesAsync(); }));
            }
        });
    }

    private void QueueAutoSave()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(PersistToggles));
        }, TaskScheduler.Default);
    }

    private void PersistToggles()
    {
        var s = _settings.Settings;
        s.Language = Language; s.Theme = Theme; s.HideOnLaunch = HideOnLaunch; s.CloseInsteadOfHide = CloseInsteadOfHide;
        s.ReopenAfterExit = ReopenAfterExit; s.ShowGameLog = ShowGameLog; s.AutoStart = AutoStart;
        s.DefaultMaxRamMb = Math.Max(1024, DefaultMaxRam);
        SavePrivSettings(s);
        Loc.I.Language = Language; App.ApplyTheme(Theme);
        try { Autostart.Set(s.AutoStart); } catch (Exception ex) { Log.Warn("Автозапуск: " + ex.Message); }
        _settings.Save();
    }

    private void OpenDiscord()
    {
        var url = BuiltIn.DiscordUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            Dialogs.Info("Ссылка на Discord пока не задана.\n\nРазработчик указывает её один раз в keys.props → DiscordUrl и пересобирает лаунчер.");
            return;
        }
        OpenUrl(url);
    }

    // =====================================================================
    //  Новости лаунчера
    // =====================================================================
    private List<InfoService.LauncherNewsEntry> _newsRaw = new();

    public async Task RefreshLauncherNewsAsync()
    {
        var url = !string.IsNullOrWhiteSpace(NewsUrlOverride) ? NewsUrlOverride.Trim() : BuiltIn.NewsUrl;
        try { _newsRaw = await _info.GetLauncherNewsAsync(url, CancellationToken.None); }
        catch (Exception ex) { Log.Warn("Новости: " + ex.Message); }
        RebuildNews();
    }

    private void RebuildNews()
    {
        bool en = Loc.I.Language == "en";
        News.Clear();
        foreach (var e in _newsRaw.OrderByDescending(x => x.Date, StringComparer.Ordinal))
        {
            var title = en ? (e.TitleEn != "" ? e.TitleEn : e.TitleRu) : (e.TitleRu != "" ? e.TitleRu : e.TitleEn);
            var text = en ? (e.TextEn != "" ? e.TextEn : e.TextRu) : (e.TextRu != "" ? e.TextRu : e.TextEn);
            News.Add(new NewsItem
            {
                Title = title, Short = text,
                Date = DateTime.TryParse(e.Date, out var d) ? d.ToString("dd.MM.yyyy") : e.Date
            });
        }
    }

    // =====================================================================
    //  Поведение при запуске игры
    // =====================================================================
    private static void StartRelaunchHelper(int gamePid)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--relaunch");
            psi.ArgumentList.Add(gamePid.ToString());
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("Не удалось запустить помощника повторного открытия", ex); }
    }

    private static void RestoreWindow()
    {
        var w = Application.Current.MainWindow;
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private static async Task RunCommandAsync(string command, string workDir, bool wait)
    {
        try
        {
            Directory.CreateDirectory(workDir);
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            using var p = Process.Start(psi);
            if (wait && p != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            }
        }
        catch (Exception ex) { Log.Warn("Команда «" + command + "»: " + ex.Message); }
    }

    private void AutoLaunchIfRequested()
    {
        var id = App.LaunchInstanceId;
        if (id == null) return;
        App.LaunchInstanceId = null;
        var inst = Instances.FirstOrDefault(i => i.Id == id);
        if (inst == null || CurrentAccount == null) return;
        SelectedInstance = inst;
        if (PlayCommand.CanExecute(null)) PlayCommand.Execute(null);
    }

    // =====================================================================
    //  Функции «как в других лаунчерах»
    // =====================================================================
    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Открытие папки: " + ex.Message); }
    }

    private void ClearCache()
    {
        try
        {
            if (Directory.Exists(AppPaths.Cache))
                foreach (var f in Directory.GetFileSystemEntries(AppPaths.Cache))
                {
                    if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f);
                }
            StatusText = "Кэш очищен";
        }
        catch (Exception ex) { Dialogs.Error("Не удалось очистить кэш: " + ex.Message); }
    }

    private void ResetSettings()
    {
        if (!Dialogs.Confirm("Сбросить все настройки лаунчера? (Версии, миры и аккаунт останутся.)", danger: true)) return;
        try { if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile); } catch { }
        var acc = CurrentAccount;
        _loadingSettings = true;
        _settings.Load();
        var s = _settings.Settings;
        Language = s.Language; Theme = s.Theme; GameRoot = s.GameRoot ?? ""; Proxy = s.Proxy ?? ""; UpdateRepo = s.UpdateRepo;
        MsClientId = s.MicrosoftClientId; CurseForgeKey = s.CurseForgeApiKey; NewsUrlOverride = s.NewsUrl;
        HideOnLaunch = s.HideOnLaunch; CloseInsteadOfHide = s.CloseInsteadOfHide; ReopenAfterExit = s.ReopenAfterExit;
        ShowGameLog = s.ShowGameLog; AutoStart = s.AutoStart; DefaultMaxRam = s.DefaultMaxRamMb; LoadPrivSettings(s);
        Loc.I.Language = s.Language; App.ApplyTheme(s.Theme); Http.Configure(s.Proxy);
        try { Autostart.Set(false); } catch { }
        CurrentAccount = acc;
        _loadingSettings = false;
        StatusText = "Настройки сброшены";
    }

    private async Task RepairAsync()
    {
        var inst = _inst ?? throw new InvalidOperationException(Loc.T("pick_instance"));
        await RunBusyAsync(async (ct, prog) =>
        {
            StatusText = "Проверка и восстановление файлов…";
            await _versions.InstallAsync(inst.LaunchVersionId, prog, ct);
            StatusText = "Файлы проверены ✔";
            OnChanged(nameof(InstanceStatusText));
        });
    }

    private void AutoRam()
    {
        if (_inst == null) return;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        int max = (int)Math.Clamp(total / 2 / 512 * 512, 2048, 16384);   // половина ОЗУ, шаг 512 МБ
        _inst.MinRamMb = Math.Min(1024, max);
        _inst.MaxRamMb = max;
        OnChanged(nameof(SelectedInstance));
        StatusText = $"RAM: {max} МБ (ОЗУ компьютера: {total} МБ)";
    }

    private void CloneInstance()
    {
        if (_inst == null) return;
        try
        {
            var copy = _instances.Clone(_inst);
            Instances.Add(copy); SelectedInstance = copy;
            StatusText = $"Создана копия: {copy.Name}";
        }
        catch (Exception ex) { Log.Error("Клонирование", ex); Dialogs.Error("Не удалось создать копию: " + ex.Message); }
    }

    private void BackupWorlds()
    {
        if (_inst == null) return;
        try
        {
            var zip = _instances.BackupWorlds(_inst);
            if (zip == null) { Dialogs.Info("В этой версии пока нет миров."); return; }
            StatusText = "Резервная копия миров: " + zip;
            OpenPath(Path.GetDirectoryName(zip)!);
        }
        catch (Exception ex) { Log.Error("Бэкап", ex); Dialogs.Error("Не удалось сохранить копию миров: " + ex.Message); }
    }

    private void CreateDesktopShortcut()
    {
        if (_inst == null) return;
        try
        {
            var safe = string.Concat(_inst.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var exe = Environment.ProcessPath!;
            Shortcut.Create(Path.Combine(Shortcut.Desktop, safe + ".lnk"), exe, $"--launch {_inst.Id}", Path.GetDirectoryName(exe), exe,
                $"Запустить {_inst.Name} через FrogLauncher");
            StatusText = $"Ярлык «{safe}» создан на рабочем столе";
        }
        catch (Exception ex) { Log.Error("Ярлык", ex); Dialogs.Error("Не удалось создать ярлык: " + ex.Message); }
    }

    private async Task UpdateModsAsync()
    {
        var inst = _inst ?? throw new InvalidOperationException(Loc.T("pick_instance"));
        await RunBusyAsync(async (ct, prog) =>
        {
            StatusText = "Проверка обновлений модов…";
            var updated = await _modSvc.UpdateAllAsync(inst, prog, ct);
            ReloadMods();
            StatusText = updated.Count == 0 ? "Все моды актуальны ✔" : "Обновлено: " + string.Join(", ", updated);
            ModStatus = StatusText;
        });
    }
}
