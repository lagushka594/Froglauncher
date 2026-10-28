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
public partial class ModpacksViewModel(
    ModpackService modpacks,
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
            var hits = await modpacks.SearchAsync(Query, LoaderFilter, VersionFilterValue, _offset, CancellationToken.None);
            if (!append) Results.Clear();
            foreach (var h in hits) Results.Add(new ModpackItem(h));
            _offset += hits.Count;
            CanLoadMore = hits.Count == ModpackService.PageSize;
            if (!append && hits.Count == 0) toasts.Show(L.T("Сборок не нашлось — попробуйте другой запрос или фильтр"), ToastKind.Info);
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

    [RelayCommand]
    private static void Compare() => WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Subscription));

    [RelayCommand]
    private static void OpenOnModrinth(ModpackItem? item)
    {
        if (item is not null) Shell.Open($"https://modrinth.com/modpack/{item.Hit.Slug}");
    }
}
