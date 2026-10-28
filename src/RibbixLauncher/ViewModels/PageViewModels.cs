using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.ViewModels;

/// <summary>«Каталог»: версии игрока (инстансы), добавление (Minecraft + модлоадер), настройки.</summary>
public partial class InstancesViewModel(
    InstanceService instances,
    VersionService versions,
    ModLoaderService loaders,
    EntitlementService entitlements,
    SettingsService settings,
    ModrinthService modrinth,
    JavaService java,
    ToastService toasts,
    WorldService worlds,
    LaunchService launcher,
    PackTransferService transfer,
    ModToolsService modTools,
    FriendLinkService friends,
    PlayStatsService playStats,
    FpsBenchmarkService bench,
    GameAuthService accounts,
    Lazy<ModpacksViewModel> catalog,
    OptimizeService optimizer,
    ILogger<InstancesViewModel> log) : ObservableObject, IPageActivated, IRefreshable
{
    /// <summary>Каталог Modrinth сразу для этой версии: моды, ресурспаки, шейдеры, датапаки.</summary>
    [RelayCommand]
    private void OpenModrinth(string? type)
    {
        catalog.Value.Show(type ?? "mod", Selected);
        WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Modpacks));
    }

    [ObservableProperty] private bool _isOptimizing;

    /// <summary>«Открыть мир друзьям»: мод e4mc даёт открытому для сети миру публичный адрес — без проброса портов.</summary>
    [RelayCommand]
    private async Task HostWorldAsync()
    {
        if (Selected is not { } inst || IsOptimizing) return;
        if (inst.Loader == LoaderType.Vanilla)
        {
            toasts.Show(L.T("Нужна версия с Fabric, Quilt, Forge или NeoForge — e4mc это мод. Создайте такую версию в «Каталоге»."), ToastKind.Info, 8);
            return;
        }
        IsOptimizing = true;
        try
        {
            if (!inst.InstalledMods.Any(m => m.Title.Contains("e4mc", StringComparison.OrdinalIgnoreCase)) &&
                !System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(inst.GameDirectory, "mods")).Any(f => System.IO.Path.GetFileName(f).StartsWith("e4mc", StringComparison.OrdinalIgnoreCase)))
            {
                await modrinth.InstallAsync(inst, "e4mc", CancellationToken.None);
                instances.Save(inst);
                RefreshModFiles();
            }
            System.Windows.MessageBox.Show(L.T("Готово! Как открыть мир друзьям:\n\n1. Нажмите «Играть» и зайдите в свой мир.\n2. Esc → «Открыть для сети» → «Начать».\n3. Лаунчер скопирует адрес вида xxx.e4mc.link и покажет его — отправьте друзьям.\n4. Друзья: «Сетевая игра» → «Прямое подключение» → адрес. У них должна быть та же версия Minecraft (и моды, если они нужны миру).\n\nВ Discord у вас появится кнопка «Присоединиться»."),
                "Ribbix Launcher", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            toasts.Show(ex is ModsException ? ex.Message : L.T("Не удалось скачать мод: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    /// <summary>«🚀 Ускорить»: набор модов оптимизации под загрузчик версии (со снимком для отката).</summary>
    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (Selected is not { } inst || IsOptimizing) return;
        if (launcher.Current is { Process.HasExited: false } run && run.Instance.Id == inst.Id)
        {
            toasts.Show(L.T("Закройте игру перед обновлением модов"), ToastKind.Info);
            return;
        }
        IsOptimizing = true;
        try
        {
            var r = await optimizer.RunAsync(inst, new Progress<string>(s => toasts.Show(s, ToastKind.Info, 2)), CancellationToken.None);
            RefreshModFiles();
            RefreshSnapshot();
            var text = r.Installed.Count > 0
                ? L.T($"🚀 Ускорено! Поставлено: {string.Join(", ", r.Installed)}.")
                : L.T("Все оптимизации уже стоят 🐸");
            if (r.Unavailable.Count > 0) text += L.T($" Нет под эту версию: {string.Join(", ", r.Unavailable)}.");
            if (r.Installed.Count > 0) text += L.T(" Замерьте FPS во вкладке «Графика» — будет видно разницу.");
            toasts.Show(text, ToastKind.Success, 9);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or System.IO.IOException or TaskCanceledException)
        {
            toasts.Show(ex is ModsException ? ex.Message : L.T("Не удалось скачать мод: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    // ---------- Обновление модов со снимком и откатом ----------
    public ObservableCollection<ModUpdate> ModUpdates { get; } = new();
    public ProgressState ModUpdateProgress { get; } = new();
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private string? _updatesText;
    [ObservableProperty] private string? _snapshotText;

    private void RefreshSnapshot() =>
        SnapshotText = Selected is { } inst && modTools.Snapshots(inst).FirstOrDefault() is { } s
            ? L.T($"Снимок модов от {s.CreatedAt.LocalDateTime:dd.MM HH:mm}: {s.Reason}")
            : null;

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (Selected is not { } inst || IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        UpdatesText = null;
        try
        {
            ModUpdates.ReplaceWith(await modTools.CheckUpdatesAsync(inst, CancellationToken.None));
            UpdatesText = ModUpdates.Count == 0 ? L.T("Все моды свежие 🐸") : L.T($"Есть обновления: {ModUpdates.Count}");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            toasts.Show(L.T("Modrinth недоступен, попробуйте позже"), ToastKind.Error);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    /// <summary>«Обновить всё»: сначала снимок папки mods, если после обновления игра упадёт — «Вернуть как было».</summary>
    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        if (Selected is not { } inst || ModUpdates.Count == 0 || ModUpdateProgress.IsActive) return;
        if (launcher.Current is { Process.HasExited: false } run && run.Instance.Id == inst.Id)
        {
            toasts.Show(L.T("Закройте игру перед обновлением модов"), ToastKind.Info);
            return;
        }
        try
        {
            var count = ModUpdates.Count;
            await modTools.ApplyUpdatesAsync(inst, ModUpdates.ToList(), new Progress<DownloadProgress>(ModUpdateProgress.Apply), CancellationToken.None);
            toasts.Show(L.T($"Обновлено модов: {count}. Снимок сохранён — если игра упадёт, «Вернуть как было» 🐸"), ToastKind.Success, 7);
            ModUpdates.Clear();
            UpdatesText = null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException or InvalidOperationException)
        {
            log.LogWarning(ex, "Обновление модов");
            toasts.Show(L.T("Не удалось обновить моды: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            ModUpdateProgress.Reset();
            RefreshModFiles();
            RefreshSnapshot();
        }
    }

    [RelayCommand]
    private void RollbackMods()
    {
        if (Selected is not { } inst || modTools.Snapshots(inst).FirstOrDefault() is not { } snap) return;
        if (MessageBox.Show(L.T($"Вернуть папку mods к снимку от {snap.CreatedAt.LocalDateTime:dd.MM HH:mm}? Текущие моды тоже сохранятся снимком."),
                "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        modTools.Restore(inst, snap);
        RefreshModFiles();
        RefreshSnapshot();
        toasts.Show(L.T("Моды возвращены как было ↩"), ToastKind.Success);
    }

    /// <summary>F5: заново скачать список версий Mojang и перечитать инстансы.</summary>
    public async Task RefreshPageAsync()
    {
        try
        {
            _all = (await versions.GetManifestAsync(force: true)).Versions;
            ApplyFilter();
            toasts.Show(L.T("Список версий обновлён"), ToastKind.Success, 2);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            toasts.Show(L.T("Не удалось обновить список версий"), ToastKind.Error);
        }
        // Перезаполнение списка сбрасывает выбор — восстанавливаем выбранную версию.
        var selectedId = Selected?.Id;
        Instances.ReplaceWith(instances.List());
        Selected = Instances.FirstOrDefault(i => i.Id == selectedId) ?? Instances.FirstOrDefault();
    }

    /// <summary>Экран «Настройки версии» (вкладки) для выбранной версии.</summary>
    [ObservableProperty] private InstanceSettingsViewModel? _settingsPanel;
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
            ? L.T($"{Instances.Count} из {max} · {TierNames.Display(entitlements.Tier)}")
            : L.T($"{Instances.Count} · без ограничений");
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
                toasts.Show(L.T("Не удалось загрузить список версий: ") + ex.Message, ToastKind.Error);
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
            if (list.Count == 0) toasts.Show(L.T($"{NewLoader} не поддерживает {NewVersion.Id}"), ToastKind.Warning);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Список версий {Loader}", NewLoader);
            toasts.Show(L.T($"{NewLoader}: не удалось получить версии"), ToastKind.Error);
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

    // ---------- Импорт сборок и миров ----------
    public ProgressState ImportProgress { get; } = new();
    [ObservableProperty] private bool _isImporting;

    [RelayCommand]
    private async Task ImportPackAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = L.T("Сборки и миры|*.mrpack;*.zip|Все файлы|*.*"),
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) await ImportFileAsync(f);
    }

    /// <summary>Файл из диалога или перетащенный на «Каталог»: сборка → новая версия, мир → в выбранную версию.</summary>
    public async Task ImportFileAsync(string path)
    {
        if (IsImporting) return;
        if (PackTransferService.Detect(path) == ImportKind.World)
        {
            if (Selected is not { } target)
            {
                toasts.Show(L.T("Это мир — сначала выберите версию, в которую его добавить"), ToastKind.Info);
                return;
            }
            if (MessageBox.Show(L.T($"«{System.IO.Path.GetFileName(path)}» — это мир Minecraft. Добавить его в «{target.Name}»?"), "Ribbix Launcher",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var folder = await Task.Run(() => worlds.Import(target, path));
                toasts.Show(L.T($"Мир «{folder}» добавлен в «{target.Name}» 🌍"), ToastKind.Success);
                if (SettingsPanel is { } panel && panel.Instance.Id == target.Id) await panel.Worlds.LoadAsync();
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or ModsException or UnauthorizedAccessException)
            {
                toasts.Show(L.T("Не удалось импортировать мир: ") + ex.Message, ToastKind.Error, 7);
            }
            return;
        }

        IsImporting = true;
        try
        {
            var progress = new Progress<DownloadProgress>(ImportProgress.Apply);
            ImportProgress.Apply(DownloadProgress.Indeterminate(L.T("Импортируем сборку")));
            var inst = await transfer.ImportAsync(path, settings.Current.DefaultRamMb, progress, CancellationToken.None);
            await OnActivatedAsync();
            Selected = Instances.FirstOrDefault(i => i.Id == inst.Id) ?? Selected;
            toasts.Show(L.T($"Сборка «{inst.Name}» импортирована ({inst.DisplayVersion}). Жмите «Играть» на главной 🐸"), ToastKind.Success, 7);
        }
        catch (InstanceLimitException ex)
        {
            Upsell = ex.Message;
        }
        catch (Exception ex) when (ex is ModsException or System.IO.IOException or System.IO.InvalidDataException or System.Net.Http.HttpRequestException
                                       or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Импорт {File} не удался", path);
            toasts.Show(L.T("Импорт не удался: ") + ex.Message, ToastKind.Error, 9);
        }
        finally
        {
            IsImporting = false;
            ImportProgress.Reset();
        }
    }

    // ---------- Для командной палитры ----------
    public void SelectById(string id) => Selected = Instances.FirstOrDefault(i => i.Id == id) ?? Selected;

    /// <summary>Открыть форму создания сразу с выбранной версией Minecraft.</summary>
    public void StartCreateWith(ManifestEntry version)
    {
        StartCreate();
        if (!IsCreating) return;
        if (version.Type == "snapshot") ShowSnapshot = true;
        else if (version.Type != "release") ShowOld = true;
        NewVersion = Versions.FirstOrDefault(v => v.Id == version.Id) ?? NewVersion;
    }

    public void OpenModSearch(string instanceId, string query)
    {
        SelectById(instanceId);
        if (!SelectedHasLoader) return;
        ShowModCatalog = true;
        ModQuery = query;
        _ = SearchModsAsync();
    }

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
            toasts.Show(L.T($"Версия «{inst.Name}» добавлена. Жмите «Играть» на главной 🐸"), ToastKind.Success);
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
        toasts.Show(L.T("Настройки версии сохранены"), ToastKind.Success);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var r = MessageBox.Show(L.T($"Удалить «{Selected.Name}» вместе с мирами и модами?"), "Ribbix Launcher",
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe", Title = L.T("Выберите Java") };
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
        SettingsPanel = value is null ? null : new InstanceSettingsViewModel(value, instances, java, entitlements, toasts,
            new WorldsViewModel(value, worlds, launcher, toasts), transfer, friends,
            playStats.Load().Sessions.LastOrDefault(s => s.InstanceId == value.Id && s.Server is not null)?.Server, bench, accounts);
        ShowModCatalog = false;
        ModResults.Clear();
        OnPropertyChanged(nameof(SelectedHasLoader));
        RefreshModFiles();
        ModUpdates.Clear();
        UpdatesText = null;
        RefreshSnapshot();
    }

    /// <summary>Все jar в папке mods: из каталога — с названием и иконкой, добавленные вручную — по имени файла.</summary>
    private void RefreshModFiles()
    {
        ModFiles.Clear();
        if (Selected is not { } inst) return;
        var dir = System.IO.Path.Combine(inst.GameDirectory, "mods");
        if (!System.IO.Directory.Exists(dir)) return;
        var files = System.IO.Directory.GetFiles(dir, "*.jar").Concat(System.IO.Directory.GetFiles(dir, "*.jar.disabled"));
        foreach (var file in files.Select(System.IO.Path.GetFileName).OfType<string>().Order())
        {
            var enabled = !file.EndsWith(".disabled", StringComparison.Ordinal);
            var jar = enabled ? file : file[..^".disabled".Length];
            var meta = inst.InstalledMods.FirstOrDefault(m => m.FileName == jar);
            ModFiles.Add(new ModFileItem(file, meta?.Title ?? jar, meta?.IconUrl, meta?.IsDependency == true, enabled));
        }
        ModsHint = ModFiles.Count == 0 ? L.T("Модов пока нет — найдите их в каталоге.") : null;
    }

    [RelayCommand]
    private async Task ToggleModCatalogAsync()
    {
        if (!SelectedHasLoader)
        {
            toasts.Show(L.T("Для модов нужна версия с модлоадером (Fabric, Quilt, Forge, NeoForge)"), ToastKind.Info, 6);
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
            if (hits.Count == 0) toasts.Show(L.T($"Ничего не нашлось для {inst.GameVersion} · {inst.Loader}"), ToastKind.Info);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            toasts.Show(ex is ModsException ? ex.Message : L.T("Modrinth недоступен, попробуйте позже"), ToastKind.Error);
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
            toasts.Show(deps > 0 ? L.T($"«{item.Hit.Title}» установлен + {deps} зависимост(и) 🐸") : L.T($"«{item.Hit.Title}» установлен 🐸"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is ModsException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            log.LogWarning(ex, "Установка мода {Project}", item.Hit.ProjectId);
            toasts.Show(ex is ModsException ? ex.Message : L.T("Не удалось скачать мод: ") + ex.Message, ToastKind.Error, 6);
        }
        finally
        {
            item.IsInstalling = false;
        }
    }

    /// <summary>Выключить мод, не удаляя: name.jar ↔ name.jar.disabled (Minecraft грузит только .jar).</summary>
    [RelayCommand]
    private void ToggleMod(ModFileItem? mod)
    {
        if (mod is null || Selected is not { } inst) return;
        var dir = System.IO.Path.Combine(inst.GameDirectory, "mods");
        var src = System.IO.Path.Combine(dir, mod.FileName);
        var dst = mod.Enabled ? src + ".disabled" : src[..^".disabled".Length];
        if (System.IO.File.Exists(src) && !System.IO.File.Exists(dst)) System.IO.File.Move(src, dst);
        instances.Save(inst);
        RefreshModFiles();
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
public sealed record ModFileItem(string FileName, string Title, string? IconUrl, bool IsDependency, bool Enabled = true)
{
    public string Subtitle => (Enabled ? "" : L.T("выключен · ")) + (IsDependency ? L.T("зависимость · ") : "") + FileName;
}

/// <summary>Результат поиска в каталоге модов.</summary>
public partial class ModHitItem(ModHit hit, bool installed) : ObservableObject
{
    public ModHit Hit { get; } = hit;
    public string DownloadsText => Hit.Downloads >= 1_000_000 ? L.T($"{Hit.Downloads / 1_000_000.0:0.#} млн загрузок")
        : Hit.Downloads >= 1000 ? L.T($"{Hit.Downloads / 1000.0:0.#} тыс. загрузок") : L.T($"{Hit.Downloads} загрузок");

    [ObservableProperty] private bool _isInstalled = installed;
    [ObservableProperty] private bool _isInstalling;
}

/// <summary>«Java»: найденные рантаймы и загрузка Temurin 8/17/21.</summary>
public partial class JavaViewModel(JavaService java, DiscordPresenceService presence, ToastService toasts) : ObservableObject, IPageActivated, IRefreshable
{
    public Task RefreshPageAsync() => RefreshAsync();

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
            await presence.SetAsync(new PresenceState(L.T("В лаунчере"), L.T($"Устанавливает Java {m}"), SmallImage: "java", SmallText: "Java"));
            await java.DownloadTemurinAsync(m, new Progress<DownloadProgress>(Progress.Apply), CancellationToken.None);
            toasts.Show(L.T($"Java {m} установлена ☕"), ToastKind.Success);
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

/// <summary>«Профиль»: игровые аккаунты (оффлайн/Microsoft) и аккаунт RibbixLauncher для подписки.</summary>
public partial class ProfileViewModel(
    GameAuthService accounts,
    RibbixApiClient api,
    SessionStore session,
    EntitlementService entitlements,
    DiscordAuthService discord,
    ToastService toasts,
    StatsViewModel stats,
    ScreenshotsViewModel screenshots,
    YearRecapViewModel recap) : ObservableObject, IPageActivated
{
    public StatsViewModel Stats { get; } = stats;
    public ScreenshotsViewModel Screenshots { get; } = screenshots;
    public YearRecapViewModel Recap { get; } = recap;

    public const int RecapTab = 3;

    /// <summary>0 — аккаунты, 1 — статистика, 2 — скриншоты, 3 — «Мой год» (грузятся при открытии вкладки).</summary>
    [ObservableProperty] private int _tab;

    partial void OnTabChanged(int value) => LoadTab();

    private void LoadTab()
    {
        if (Tab == 1) Stats.Load();
        else if (Tab == 2) _ = Screenshots.LoadAsync();
        else if (Tab == RecapTab) Recap.Load();
    }

    private CancellationTokenSource? _msCts;
    private CancellationTokenSource? _discordCts;

    /// <summary>Тариф берётся из ролей Discord — вместо аккаунта RibbixLauncher показываем вход через Discord.</summary>
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
            ? L.T("Вы не состоите на нашем Discord-сервере — вступите, чтобы тариф по роли включился.")
            : null;
        LoadTab();
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
            toasts.Show(L.T($"Discord подключён: {s.DisplayName}. Тариф: {TierNames.Display(entitlements.Tier)} 🐸"), ToastKind.Success, 6);
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
            toasts.Show(L.T("Оффлайн-аккаунт добавлен"), ToastKind.Success);
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
            toasts.Show(L.T($"Вошли как {acc.Name} 🐸"), ToastKind.Success);
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
            toasts.Show(L.T("Аккаунт Ribbix подключён"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException)
        {
            toasts.Show(ex is ApiException ? ex.Message : L.T("Сервер недоступен"), ToastKind.Error);
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
    UpdateService updates,
    HotkeyService hotkeys,
    ToastService toasts) : ObservableObject, IPageActivated
{
    // ---------- Обновления ----------
    public string CurrentVersion => UpdateService.CurrentVersion;
    public bool CanUseBeta => updates.CanUseBeta;
    public bool IsInstalledCopy => UpdateService.IsInstalledCopy;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _betaChannel;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _checkingUpdate;

    // ---------- Сезоны ----------
    [ObservableProperty] private bool _seasonEffects;
    [ObservableProperty] private bool _startupSound;

    partial void OnSeasonEffectsChanged(bool value)
    {
        settings.Current.SeasonEffects = value;
        settings.Save();
        (App.Services.GetService(typeof(MainViewModel)) as MainViewModel)?.UpdateEffectsPaused();
    }

    partial void OnStartupSoundChanged(bool value)
    {
        settings.Current.StartupSound = value;
        settings.Save();
    }

    // ---------- Трей и напоминания ----------
    public IReadOnlyList<string> ReminderOptions { get; } = new[] { L.T("Не напоминать"), L.T("Каждый час"), L.T("Каждые 2 часа"), L.T("Каждые 3 часа") };
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private int _reminderHours;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        settings.Current.MinimizeToTray = value;
        settings.Save();
    }

    partial void OnReminderHoursChanged(int value)
    {
        settings.Current.PlaytimeReminderHours = Math.Clamp(value, 0, 3);
        settings.Save();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        settings.Current.AutoUpdate = value;
        settings.Save();
    }

    partial void OnBetaChannelChanged(bool value)
    {
        settings.Current.UpdateChannel = value && CanUseBeta ? "beta" : "stable";
        settings.Save();
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        CheckingUpdate = true;
        UpdateStatus = L.T("Проверяем GitHub…");
        try
        {
            settings.Current.SkippedUpdate = null;
            var info = IsInstalledCopy ? await updates.CheckAndDownloadAsync(CancellationToken.None) : await updates.CheckAsync(CancellationToken.None);
            UpdateStatus = info switch
            {
                null => L.T($"У вас последняя версия ({CurrentVersion}, канал {updates.Channel}) 🐸"),
                _ when updates.Ready is not null => L.T($"Версия {info.Version} скачана — нажмите «Обновить и перезапустить» вверху окна"),
                _ when info.Sha256 is null => L.T($"Есть версия {info.Version}, но у релиза нет SHA-256 — скачайте вручную со страницы релиза"),
                _ when !IsInstalledCopy => L.T($"Есть версия {info.Version}. Автообновление работает в установленном лаунчере (не из папки разработки)"),
                _ => L.T($"Есть версия {info.Version}"),
            };
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            UpdateStatus = L.T("GitHub недоступен: ") + ex.Message;
        }
        finally
        {
            CheckingUpdate = false;
        }
    }

    // ---------- Горячие клавиши ----------
    public ObservableCollection<HotkeyItem> Hotkeys { get; } = new();

    public void SetHotkey(HotkeyItem item, string gesture)
    {
        hotkeys.Set(item.Id, gesture);
        LoadHotkeys();
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        hotkeys.Reset();
        LoadHotkeys();
    }

    private void LoadHotkeys() =>
        Hotkeys.ReplaceWith(HotkeyService.Actions.Select(a => new HotkeyItem(a.Id, a.Label, hotkeys.Get(a.Id))));

    public ObservableCollection<ThemeItem> Themes { get; } = new();

    // ---------- Язык ----------
    public IReadOnlyList<LanguageInfo> LanguageChoices { get; } =
        new[] { new LanguageInfo("auto", L.T("Как в системе"), "") }.Concat(L.Languages).ToList();
    [ObservableProperty] private LanguageInfo? _language;
    [ObservableProperty] private bool _languageRestartNeeded;

    partial void OnLanguageChanged(LanguageInfo? value)
    {
        if (value is null || value.Code == settings.Current.Language) return;
        settings.Current.Language = value.Code;
        settings.Save();
        LanguageRestartNeeded = L.Resolve(value.Code, System.Globalization.CultureInfo.InstalledUICulture) != L.Language;
    }

    [RelayCommand]
    private static void RestartForLanguage() => App.Restart();

    public LauncherSettings Settings => settings.Current;

    [ObservableProperty] private bool _discordPresence;
    [ObservableProperty] private bool _closeOnLaunch;
    [ObservableProperty] private int _defaultRamMb;

    public Task OnActivatedAsync()
    {
        Themes.ReplaceWith(ThemeService.Themes.Select(t => new ThemeItem(t, themes.IsUnlocked(t), t.Key == settings.Current.Theme)));
        DiscordPresence = settings.Current.DiscordPresence;
        Language = LanguageChoices.FirstOrDefault(l => l.Code == settings.Current.Language) ?? LanguageChoices[0];
        CloseOnLaunch = settings.Current.CloseOnLaunch;
        AutoUpdate = settings.Current.AutoUpdate;
        MinimizeToTray = settings.Current.MinimizeToTray;
        SeasonEffects = settings.Current.SeasonEffects;
        StartupSound = settings.Current.StartupSound;
        ReminderHours = Math.Clamp(settings.Current.PlaytimeReminderHours, 0, 3);
        BetaChannel = settings.Current.UpdateChannel == "beta" && CanUseBeta;
        OnPropertyChanged(nameof(CanUseBeta));
        LoadHotkeys();
        DefaultRamMb = settings.Current.DefaultRamMb;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ApplyThemeAsync(ThemeItem item)
    {
        if (!item.Unlocked)
        {
            toasts.Show(L.T($"Тема «{item.Theme.Name}» доступна с {item.Theme.RequiresLabel}"), ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));
            return;
        }

        if (Seasons.IsWinterInSummer(item.Theme.Key, AppClock.Today) &&
            MessageBox.Show(L.T("Ты уверен? За окном +30° 🥵"), "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

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

public partial class HotkeyItem(string id, string label, string gesture) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Gesture { get; } = gesture;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Display))] private bool _isCapturing;
    public string Display => IsCapturing ? L.T("Нажмите сочетание…") : Gesture.Length == 0 ? "—" : Gesture;
}

/// <summary>«Помощь»: о лаунчере, исходный код, юридические оговорки.</summary>
public partial class AboutViewModel(IOptions<LauncherOptions> options, OnboardingViewModel onboarding) : ObservableObject
{
    [RelayCommand]
    private void ShowOnboarding() => onboarding.Show();

    public string Version => LaunchService.LauncherVersion;
    public string GitHubUrl => options.Value.GitHubUrl;

    [RelayCommand]
    private void OpenGitHub() => Shell.Open(GitHubUrl);

    [RelayCommand]
    private void OpenDiscord() => Shell.Open(options.Value.DiscordUrl);

    [RelayCommand]
    private void OpenTikTok() => Shell.Open(options.Value.TikTokUrl);

    [RelayCommand]
    private void OpenYouTube() => Shell.Open(options.Value.YouTubeUrl);

    [RelayCommand]
    private static void OpenLogs() => Shell.Open(LauncherPaths.Logs);
}
