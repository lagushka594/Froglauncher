using FrogLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Models;
using FrogLauncher.Services;

namespace FrogLauncher.ViewModels;

/// <summary>Сервер в списке: запись каталога + живой статус.</summary>
public partial class ServerItem(ServerEntry entry, bool favorite) : ObservableObject
{
    public ServerEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Address => Entry.Address;
    public string TagsText => string.Join(" · ", Entry.Tags);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OnlineText), nameof(PingText), nameof(SortKey), nameof(IsOnline))]
    private ServerStatus? _status;
    [ObservableProperty] private BitmapImage? _icon;
    [ObservableProperty] private bool _isFavorite = favorite;
    [ObservableProperty] private bool _isPinging;

    public bool IsOnline => Status?.Online == true;
    public string OnlineText => Status is { Online: true } s ? $"{s.Players:N0} / {s.MaxPlayers:N0}" : Status?.Error ?? "…";
    public string PingText => Status is { Online: true, PingMs: >= 0 } s ? L.T($"{s.PingMs} мс") : "";
    public string VersionText => Status is { Online: true } s ? s.Version : "";
    public string Motd => Status?.Motd ?? Entry.Description;
    public int SortKey => Status is { Online: true } s ? s.Players : -1;

    public void Apply(ServerStatus s)
    {
        Status = s;
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(Motd));
        if (s.Icon is { Length: > 0 } bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                Icon = bmp;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException) { }
        }
    }
}

/// <summary>«Серверы»: топ серверов с живым онлайном и пингом, фильтры, избранное, свои серверы, «Играть» с автоподключением.</summary>
public partial class ServersViewModel(
    ServerCatalog catalog,
    InstanceService instances,
    SettingsService settings,
    HomeViewModel home,
    ToastService toasts) : ObservableObject, IPageActivated, IRefreshable
{
    public Task RefreshPageAsync() => RefreshAsync();

    public static readonly string AllTags = L.T("Все режимы");
    public static readonly string AllRegions = L.T("Все регионы");

    private List<ServerItem> _all = new();
    private bool _loaded;

    public ObservableCollection<ServerItem> Servers { get; } = new();
    public ObservableCollection<string> Tags { get; } = new() { AllTags };
    public ObservableCollection<string> Regions { get; } = new() { AllRegions };
    public ObservableCollection<Instance> Instances { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _selectedTag = AllTags;
    [ObservableProperty] private string _selectedRegion = AllRegions;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _onlineOnly;
    [ObservableProperty] private Instance? _playInstance;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _newServerName = "";
    [ObservableProperty] private string _newServerAddress = "";
    [ObservableProperty] private bool _showAdd;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedTagChanged(string value) => ApplyFilter();
    partial void OnSelectedRegionChanged(string value) => ApplyFilter();
    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilter();
    partial void OnOnlineOnlyChanged(bool value) => ApplyFilter();

    public async Task OnActivatedAsync()
    {
        Instances.ReplaceWith(instances.List());
        PlayInstance = Instances.FirstOrDefault(i => i.Id == settings.Current.SelectedInstanceId) ?? Instances.FirstOrDefault();
        if (!_loaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var favs = settings.Current.FavoriteServers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = (await catalog.LoadAsync(CancellationToken.None)).ToList();
        entries.InsertRange(0, settings.Current.CustomServers.Select(c => { c.Custom = true; return c; }));
        _all = entries.DistinctBy(e => e.Address.ToLowerInvariant()).Select(e => new ServerItem(e, favs.Contains(e.Address))).ToList();

        Tags.ReplaceWith(new[] { AllTags }.Concat(_all.SelectMany(s => s.Entry.Tags).Distinct().Order()));
        Regions.ReplaceWith(new[] { AllRegions }.Concat(_all.Select(s => s.Entry.Region).Where(r => r.Length > 0).Distinct().Order()));
        // Перезаполнение списков сбрасывает выбор ComboBox в null — возвращаем «все».
        SelectedTag = AllTags;
        SelectedRegion = AllRegions;
        _loaded = true;
        ApplyFilter();
        await RefreshAsync();
    }

    /// <summary>Пингуем все серверы параллельно (до 8 сразу) и пересортировываем по онлайну.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(_all.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    item.IsPinging = true;
                    item.Apply(await ServerPing.PingAsync(item.Address, CancellationToken.None));
                }
                finally
                {
                    item.IsPinging = false;
                    gate.Release();
                }
            }));
            ApplyFilter();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        Servers.ReplaceWith(_all
            .Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Address.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || s.Entry.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) || s.VersionText.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(s => SelectedTag is null || SelectedTag == AllTags || s.Entry.Tags.Contains(SelectedTag))
            .Where(s => SelectedRegion is null || SelectedRegion == AllRegions || s.Entry.Region == SelectedRegion)
            .Where(s => !FavoritesOnly || s.IsFavorite)
            .Where(s => !OnlineOnly || s.IsOnline)
            .OrderByDescending(s => s.IsFavorite)
            .ThenByDescending(s => s.SortKey));
    }

    [RelayCommand]
    private void ToggleFavorite(ServerItem? item)
    {
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        var favs = settings.Current.FavoriteServers;
        favs.RemoveAll(a => string.Equals(a, item.Address, StringComparison.OrdinalIgnoreCase));
        if (item.IsFavorite) favs.Add(item.Address);
        settings.Save();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task PlayAsync(ServerItem? item)
    {
        if (item is null) return;
        if (PlayInstance is null)
        {
            toasts.Show(L.T("Сначала добавьте версию в «Каталоге» или «Сборках»"), ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Catalog));
            return;
        }

        settings.Current.LastServer = item.Address;
        settings.Current.SelectedInstanceId = PlayInstance.Id;
        settings.Save();
        WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Home));
        toasts.Show(L.T($"Запускаем {PlayInstance.Name} и подключаемся к {item.Name}…"), ToastKind.Info);
        await home.PlayOnServerAsync(PlayInstance, item.Address);
    }

    [RelayCommand]
    private void ToggleAdd() => ShowAdd = !ShowAdd;

    [RelayCommand]
    private async Task AddServerAsync()
    {
        var address = NewServerAddress.Trim();
        if (address.Length is 0 or > 255 || address.Contains(' '))
        {
            toasts.Show(L.T("Введите адрес сервера, например play.example.com или 1.2.3.4:25565"), ToastKind.Warning);
            return;
        }

        var entry = new ServerEntry
        {
            Name = string.IsNullOrWhiteSpace(NewServerName) ? address : NewServerName.Trim(),
            Address = address,
            Region = "Мои", // noloc: значение хранится в настройках
            Tags = new() { "мои" }, // noloc
            Custom = true,
        };
        settings.Current.CustomServers.RemoveAll(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        settings.Current.CustomServers.Add(entry);
        if (!settings.Current.FavoriteServers.Contains(address)) settings.Current.FavoriteServers.Add(address);
        settings.Save();

        var item = new ServerItem(entry, favorite: true);
        _all.RemoveAll(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        _all.Insert(0, item);
        if (!Regions.Contains("Мои")) Regions.Add("Мои"); // noloc
        if (!Tags.Contains("мои")) Tags.Add("мои"); // noloc
        NewServerName = NewServerAddress = "";
        ShowAdd = false;
        ApplyFilter();
        item.IsPinging = true;
        item.Apply(await ServerPing.PingAsync(address, CancellationToken.None));
        item.IsPinging = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void RemoveCustom(ServerItem? item)
    {
        if (item is not { Entry.Custom: true }) return;
        settings.Current.CustomServers.RemoveAll(s => string.Equals(s.Address, item.Address, StringComparison.OrdinalIgnoreCase));
        settings.Current.FavoriteServers.RemoveAll(a => string.Equals(a, item.Address, StringComparison.OrdinalIgnoreCase));
        settings.Save();
        _all.Remove(item);
        ApplyFilter();
    }

    [RelayCommand]
    private static void CopyAddress(ServerItem? item)
    {
        if (item is null) return;
        try { System.Windows.Clipboard.SetText(item.Address); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
