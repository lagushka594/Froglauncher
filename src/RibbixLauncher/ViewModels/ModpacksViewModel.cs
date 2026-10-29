using RibbixLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

/// <summary>Карточка сборки в поиске.</summary>
public partial class ModpackItem(ModpackHit hit) : ObservableObject
{
    public ModpackHit Hit { get; } = hit;

    public string DownloadsText => Hit.Downloads >= 1_000_000 ? L.T($"{Hit.Downloads / 1_000_000.0:0.#} млн загрузок")
        : Hit.Downloads >= 1000 ? L.T($"{Hit.Downloads / 1000.0:0.#} тыс. загрузок") : L.T($"{Hit.Downloads} загрузок");

    /// <summary>«1.20.1 · Fabric» — последняя версия игры и модлоадеры сборки.</summary>
    public string Meta
    {
        get
        {
            var mc = Hit.GameVersions.LastOrDefault(v => char.IsDigit(v.FirstOrDefault()) && !v.Contains('w')) ?? Hit.GameVersions.LastOrDefault();
            var loaders = string.Join(", ", Hit.Loaders.Select(l => l switch
            {
                "neoforge" => "NeoForge", "forge" => "Forge", "quilt" => "Quilt", _ => "Fabric",
            }));
            return string.Join(" · ", new[] { mc, loaders }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isInstalled;
}

/// <summary>«Сборки»: поиск модпаков Modrinth и установка в новую версию одним кликом.</summary>
/// <summary>Раздел каталога Modrinth: modpack, mod, resourcepack, shader, datapack.</summary>
public partial class ContentKind(string type, string title, string icon) : ObservableObject
{
    public string Type { get; } = type;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    [ObservableProperty] private bool _isSelected;
}

public partial class ModpacksViewModel(
    ModpackService modpacks,
    ModrinthService modrinth,
    InstanceService instances,
    WorldService worlds,
    VersionService versions,
    SettingsService settings,
    DiscordPresenceService presence,
    ToastService toasts,
    ILogger<ModpacksViewModel> log) : ObservableObject, IPageActivated, IRefreshable
{
    public Task RefreshPageAsync() => SearchAsync();

    public static readonly string AnyVersion = L.T("Любая версия");

    public ObservableCollection<ModpackItem> Results { get; } = new();
    public ObservableCollection<string> GameVersions { get; } = new() { AnyVersion };
    public IReadOnlyList<string> LoaderFilters { get; } = new[] { L.T("Любой модлоадер"), "Fabric", "Forge", "NeoForge", "Quilt" };
    public ProgressState Progress { get; } = new();

    // ---------- Что ищем и куда ставим ----------
    public IReadOnlyList<ContentKind> Kinds { get; } = new[]
    {
        new ContentKind("modpack", L.T("Сборки"), "modpack"), new ContentKind("mod", L.T("Моды"), "mods"),
        new ContentKind("resourcepack", L.T("Ресурспаки"), "catalog"), new ContentKind("shader", L.T("Шейдеры"), "video"),
        new ContentKind("datapack", L.T("Датапаки"), "folder"),
    };
    public ObservableCollection<Instance> Targets { get; } = new();
    public ObservableCollection<string> TargetWorlds { get; } = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModpacks), nameof(IsDatapacks))] private ContentKind? _kind;
    [ObservableProperty] private Instance? _target;
    [ObservableProperty] private string? _targetWorld;
    public bool IsModpacks => Kind is null or { Type: "modpack" };
    public bool IsDatapacks => Kind?.Type == "datapack";

    partial void OnKindChanged(ContentKind? value)
    {
        foreach (var k in Kinds) k.IsSelected = k == value;
        Results.Clear();
        _ = SearchAsync();
    }

    partial void OnTargetChanged(Instance? value)
    {
        TargetWorlds.ReplaceWith(value is null ? Array.Empty<string>() : worlds.List(value).Select(w => w.Folder));
        TargetWorld = TargetWorlds.FirstOrDefault();
        settings.Current.SelectedInstanceId = value?.Id ?? settings.Current.SelectedInstanceId;
        if (!IsModpacks) _ = SearchAsync();
    }

    /// <summary>Открыть сразу нужный раздел (кнопка «Моды» в настройках версии).</summary>
    public void Show(string type, Instance? target)
    {
        Kind = Kinds.FirstOrDefault(k => k.Type == type) ?? Kinds[0];
        if (target is not null) Target = Targets.FirstOrDefault(t => t.Id == target.Id) ?? Target;
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _selectedLoader = L.T("Любой модлоадер");
    [ObservableProperty] private string _selectedVersion = AnyVersion;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string? _upsell;
    private int _offset;
    [ObservableProperty] private bool _canLoadMore;

    public async Task OnActivatedAsync()
    {
        if (GameVersions.Count == 1)
        {
            try
            {
                foreach (var v in (await versions.ListAsync(VersionFilter.Release)).Select(v => v.Id)) GameVersions.Add(v);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { /* без списка — только «Любая» */ }
        }
        var keep = Target?.Id ?? settings.Current.SelectedInstanceId;
        Targets.ReplaceWith(instances.List());
        Target = Targets.FirstOrDefault(t => t.Id == keep) ?? Targets.FirstOrDefault();
        Kind ??= Kinds[1]; // по умолчанию — моды
        if (Results.Count == 0) await SearchAsync();
    }

    partial void OnSelectedLoaderChanged(string value) => _ = SearchAsync();
    partial void OnSelectedVersionChanged(string value) => _ = SearchAsync();

    private LoaderType? LoaderFilter => SelectedLoader switch
    {
        "Fabric" => LoaderType.Fabric,
        "Forge" => LoaderType.Forge,
        "NeoForge" => LoaderType.NeoForge,
        "Quilt" => LoaderType.Quilt,
        _ => null,
    };

    private string? VersionFilterValue => SelectedVersion == AnyVersion ? null : SelectedVersion;

    public void SearchFor(string query)
    {
        Query = query;
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _offset = 0;
        await LoadAsync(append: false);
    }

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(append: true);

    private async Task LoadAsync(bool append)
    {
        if (IsSearching) return;
        IsSearching = true;
        try
        {
            // Сборки — по фильтрам страницы; остальное — под выбранную версию (её Minecraft, для модов — и загрузчик).
            var type = Kind?.Type ?? "modpack";
            var hits = type == "modpack"
                ? await modpacks.SearchAsync(Query, LoaderFilter, VersionFilterValue, _offset, CancellationToken.None)
                : await modpacks.SearchAsync(Query, type == "mod" ? Target?.Loader is { } l && l != LoaderType.Vanilla ? l : null : null,
                    Target?.GameVersion, _offset, CancellationToken.None, type);
            if (!append) Results.Clear();
            foreach (var h in hits) Results.Add(new ModpackItem(h));
            _offset += hits.Count;
            CanLoadMore = hits.Count == ModpackService.PageSize;
            if (!append && hits.Count == 0) toasts.Show(L.T("Ничего не нашлось — попробуйте другой запрос или фильтр"), ToastKind.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            toasts.Show(L.T("Modrinth недоступен, попробуйте позже"), ToastKind.Error);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(ModpackItem? item)
    {
        if (item is null || IsInstalling) return;
        if (!IsModpacks)
        {
            await InstallContentAsync(item);
            return;
        }
        IsInstalling = true;
        item.IsInstalling = true;
        Upsell = null;
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Apply(p);
            _ = presence.SetAsync(new PresenceState(L.T($"Устанавливает сборку {item.Hit.Title}"),
                p.TotalBytes > 0 ? $"{p.Stage}: {p.Percent:0}%" : p.Stage, SmallImage: "download", SmallText: L.T("Загрузка")));
        });

        try
        {
            var inst = await modpacks.InstallAsync(item.Hit, VersionFilterValue, settings.Current.DefaultRamMb, progress, CancellationToken.None);
            item.IsInstalled = true;
            settings.Current.SelectedInstanceId = inst.Id;
            settings.Save();
            toasts.Show(L.T($"Сборка «{inst.Name}» готова ({inst.DisplayVersion}). Жмите «Играть» на главной 🐸"), ToastKind.Success, 7);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Home));
        }
        catch (InstanceLimitException ex)
        {
            Upsell = ex.Message;
        }
        catch (Exception ex) when (ex is ModsException or HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            log.LogWarning(ex, "Установка сборки {Pack}", item.Hit.ProjectId);
            toasts.Show(L.T("Сборка не установилась: ") + ex.Message, ToastKind.Error, 8);
        }
        finally
        {
            item.IsInstalling = false;
            IsInstalling = false;
            Progress.Reset();
            await presence.SetIdleAsync();
        }
    }

    /// <summary>Мод, ресурспак, шейдер или датапак — в выбранную версию (и мир для датапака).</summary>
    private async Task InstallContentAsync(ModpackItem item)
    {
        if (Target is not { } inst)
        {
            toasts.Show(L.T("Сначала создайте версию в «Каталоге»"), ToastKind.Info);
            return;
        }
        var type = Kind!.Type;
        if (type == "mod" && inst.Loader == LoaderType.Vanilla)
        {
            toasts.Show(L.T("Для модов нужна версия с модлоадером (Fabric, Quilt, Forge, NeoForge)"), ToastKind.Info, 6);
            return;
        }
        IsInstalling = true;
        item.IsInstalling = true;
        try
        {
            if (type == "mod")
            {
                if (!await ModConflictPrompt.ConfirmAsync(modrinth, inst, item.Hit.ProjectId, item.Hit.Title, log)) return;
                var added = await modrinth.InstallAsync(inst, item.Hit.ProjectId, CancellationToken.None);
                instances.Save(inst);
                var deps = added.Count(a => a.IsDependency);
                toasts.Show(deps > 0 ? L.T($"«{item.Hit.Title}» установлен в «{inst.Name}» + {deps} зависимост(и) 🐸") : L.T($"«{item.Hit.Title}» установлен в «{inst.Name}» 🐸"), ToastKind.Success);
            }
            else
            {
                var file = await modrinth.InstallFileAsync(inst, item.Hit.ProjectId, type, TargetWorld, CancellationToken.None);
                toasts.Show(type switch
                {
                    "resourcepack" => L.T($"Ресурспак «{item.Hit.Title}» скачан в «{inst.Name}» — включите его в «Настройках версии» → «Ресурспаки»"),
                    "shader" => L.T($"Шейдер «{item.Hit.Title}» скачан в «{inst.Name}» — для шейдеров нужен Iris или OptiFine"),
                    _ => L.T($"Датапак «{item.Hit.Title}» положен в мир «{TargetWorld}»"),
                }, ToastKind.Success, 7);
                log.LogInformation("Скачан {File}", file);
            }
            item.IsInstalled = true;
        }
        catch (Exception ex) when (ex is ModsException or HttpRequestException or IOException or TaskCanceledException)
        {
            log.LogWarning(ex, "Установка {Type} {Project}", type, item.Hit.ProjectId);
            toasts.Show(ex is ModsException ? ex.Message : L.T("Не удалось скачать: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            item.IsInstalling = false;
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private static void Compare() => WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));

    [RelayCommand]
    private static void OpenOnModrinth(ModpackItem? item)
    {
        if (item is not null) Shell.Open($"https://modrinth.com/project/{item.Hit.Slug}");
    }
}
