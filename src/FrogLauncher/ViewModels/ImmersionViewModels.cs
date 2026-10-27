using FrogLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Microsoft.Extensions.Logging;

namespace FrogLauncher.ViewModels;

/// <summary>Столбик графика активности.</summary>
public sealed record DayBar(string Label, string Tooltip, double Height, bool IsToday);

public sealed record StatTile(string Value, string Label);

public sealed record VersionTile(string Name, string Time, string Sessions);

/// <summary>Строка списка достижений: прогресс по группам требований, секретные — без названия, пока не выполнены.</summary>
public sealed record AdvancementItem(string Title, string Description, string Frame, string ProgressText, double Percent, bool IsDone);

public sealed record AdvancementCategory(string Key, string Title);

/// <summary>«Профиль» → «Статистика»: часы, сессии, серия дней, бейдж, график по дням, счётчики Minecraft, достижения.</summary>
public partial class StatsViewModel(PlayStatsService stats, InstanceService instances, AdvancementService advancements) : ObservableObject
{
    public const int ChartDays = 14;
    public const double ChartHeight = 130;

    public ObservableCollection<DayBar> Days { get; } = new();
    public ObservableCollection<StatTile> Tiles { get; } = new();
    public ObservableCollection<StatTile> McTiles { get; } = new();
    public ObservableCollection<VersionTile> TopVersions { get; } = new();
    public ObservableCollection<AdvancementWorld> AdvWorlds { get; } = new();
    public ObservableCollection<AdvancementCategory> AdvCategories { get; } = new();
    public ObservableCollection<AdvancementItem> Advancements { get; } = new();

    [ObservableProperty] private AdvancementWorld? _advWorld;
    [ObservableProperty] private AdvancementCategory? _advCategory;
    [ObservableProperty] private bool _advOnlyLeft;
    [ObservableProperty] private string _advSummary = "";
    [ObservableProperty] private double _advPercent;
    private IReadOnlyList<AdvancementProgress> _adv = Array.Empty<AdvancementProgress>();

    [ObservableProperty] private string _badgeTitle = "";
    [ObservableProperty] private string _badgeEmoji = "";
    [ObservableProperty] private string _badgeProgress = "";
    [ObservableProperty] private double _badgePercent;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _chartMax = "";
    [ObservableProperty] private bool _isEmpty;

    public void Load()
    {
        var log = stats.Load();
        var sessions = log.Sessions;
        var mc = stats.ReadMinecraftStats();
        var tracked = TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks));
        // Лаунчер считает только свои сессии; время из миров Minecraft включает и игру до FrogLauncher.
        var total = tracked > mc.PlayTime ? tracked : mc.PlayTime;
        var hours = total.TotalHours;

        var badge = PlayStatsService.BadgeFor(hours);
        BadgeTitle = badge.Title;
        BadgeEmoji = badge.Emoji;
        if (badge.ToHours is { } to)
        {
            BadgePercent = Math.Clamp((hours - badge.FromHours) / (to - badge.FromHours) * 100, 0, 100);
            var next = PlayStatsService.Badges[PlayStatsService.Badges.ToList().IndexOf(badge) + 1];
            BadgeProgress = L.T($"До «{next.Title}» {next.Emoji} осталось {Hours(TimeSpan.FromHours(to - hours))}");
        }
        else
        {
            BadgePercent = 100;
            BadgeProgress = L.T("Максимальный уровень — вы легенда пруда 👑");
        }
        TotalText = Hours(total);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var daily = PlayStatsService.Daily(sessions, ChartDays, today);
        var max = Math.Max(daily.Max(d => d.Time.TotalMinutes), 1);
        ChartMax = daily.Any(d => d.Time > TimeSpan.Zero) ? L.T($"макс. {Hours(TimeSpan.FromMinutes(max))} в день") : "";
        Days.ReplaceWith(daily.Select(d => new DayBar(
            d.Day.Day.ToString(),
            L.T($"{d.Day:dd.MM, ddd}: {(d.Time > TimeSpan.Zero ? Hours(d.Time) : "не играли")}"),
            d.Time > TimeSpan.Zero ? Math.Max(3, d.Time.TotalMinutes / max * ChartHeight) : 0,
            d.Day == today)));
        IsEmpty = sessions.Count == 0;

        var longest = sessions.Count > 0 ? sessions.Max(s => s.Duration) : TimeSpan.Zero;
        var last = sessions.Count > 0 ? DateOnly.FromDateTime(sessions.Max(s => s.End ?? s.Start).LocalDateTime) : (DateOnly?)null;
        Tiles.ReplaceWith(new[]
        {
            new StatTile(last is { } l ? Ago(l, today) : "—", L.T("последняя игра")),
            new StatTile(sessions.Count.ToString("N0"), L.T("сессий во всех версиях")),
            new StatTile(Hours(longest), L.T("самая долгая сессия")),
            new StatTile(L.T($"{PlayStatsService.Streak(sessions, today)} дн."), L.T("серия дней подряд")),
            new StatTile(Hours(TimeSpan.FromTicks(daily.Sum(d => d.Time.Ticks))), L.T($"за {ChartDays} дней")),
        });
        McTiles.ReplaceWith(new[]
        {
            new StatTile(mc.Advancements.ToString("N0"), L.T("достижений выполнено")),
            new StatTile((mc.WalkCm / 100_000.0).ToString("N1") + L.T(" км"), L.T("пройдено пешком")),
            new StatTile(mc.Jumps.ToString("N0"), L.T("прыжков")),
            new StatTile(mc.MobKills.ToString("N0"), L.T("мобов побеждено")),
            new StatTile(mc.Deaths.ToString("N0"), L.T("смертей")),
            new StatTile(mc.Worlds.ToString("N0"), L.T("миров")),
        });

        var names = instances.List().ToDictionary(i => i.Id, i => i.Name);
        TopVersions.ReplaceWith(sessions.GroupBy(s => s.InstanceId)
            .Select(g => (Name: names.GetValueOrDefault(g.Key) ?? g.First().InstanceName, Time: TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)), Count: g.Count()))
            .OrderByDescending(x => x.Time).Take(5)
            .Select(x => new VersionTile(x.Name, Hours(x.Time), L.T($"{x.Count} {MainViewModel.Plural(x.Count, "сессия", "сессии", "сессий")}"))));

        LoadAdvancements();
    }

    /// <summary>«сегодня», «вчера», «3 дн. назад», дальше недели — дата.</summary>
    internal static string Ago(DateOnly day, DateOnly today) => (today.DayNumber - day.DayNumber) switch
    {
        <= 0 => L.T("сегодня"),
        1 => L.T("вчера"),
        var n and < 7 => L.T($"{n} дн. назад"),
        _ => day.ToString("dd.MM.yyyy"),
    };

    private void LoadAdvancements()
    {
        var keep = AdvWorld?.WorldDir;
        var worlds = advancements.Worlds().Where(w => advancements.Catalog(w.Instance.GameVersion).Count > 0).ToList();
        AdvWorlds.ReplaceWith(worlds);
        AdvWorld = worlds.FirstOrDefault(w => w.WorldDir == keep) ?? worlds.FirstOrDefault();
        if (AdvWorld is null) ShowAdvancements();
    }

    partial void OnAdvWorldChanged(AdvancementWorld? value)
    {
        _adv = value is null ? Array.Empty<AdvancementProgress>() : advancements.Read(value, L.Language);
        var keep = AdvCategory?.Key;
        var lang = value is null ? null : advancements.Lang(value.Instance.GameVersion, L.Language);
        AdvCategories.ReplaceWith(new[] { new AdvancementCategory("", L.T("Все вкладки")) }.Concat(
            AdvancementService.Categories.Where(c => _adv.Any(a => a.Def.Category == c))
                .Select(c => new AdvancementCategory(c, lang?.GetValueOrDefault($"advancements.{c}.root.title") ?? c))));
        AdvCategory = AdvCategories.FirstOrDefault(c => c.Key == keep) ?? AdvCategories[0];
        ShowAdvancements();
    }

    partial void OnAdvCategoryChanged(AdvancementCategory? value) => ShowAdvancements();
    partial void OnAdvOnlyLeftChanged(bool value) => ShowAdvancements();

    private void ShowAdvancements()
    {
        var done = _adv.Count(a => a.IsDone);
        AdvSummary = _adv.Count > 0 ? L.T($"{done} из {_adv.Count}") : "";
        AdvPercent = _adv.Count > 0 ? done * 100.0 / _adv.Count : 0;
        Advancements.ReplaceWith(_adv
            .Where(a => string.IsNullOrEmpty(AdvCategory?.Key) || a.Def.Category == AdvCategory.Key)
            .Where(a => !AdvOnlyLeft || !a.IsDone)
            .Select(a =>
            {
                var secret = a.Def.Hidden && !a.IsDone;
                return new AdvancementItem(
                    secret ? "???" : a.Title,
                    secret ? L.T("Секретное достижение — откроется, когда выполните") : a.Description,
                    a.Def.Frame switch { "challenge" => "🏆", "goal" => "🎯", _ => "" },
                    a.IsDone ? "✓" : $"{a.Done}/{a.Total}",
                    a.Done * 100.0 / a.Total,
                    a.IsDone);
            }));
    }

    internal static string Hours(TimeSpan t) =>
        t.TotalHours >= 1 ? L.T($"{(int)t.TotalHours} ч {t.Minutes} мин") : L.T($"{Math.Max(0, t.Minutes)} мин");
}

public partial class ScreenshotItem(Screenshot shot, BitmapImage thumb) : ObservableObject
{
    public Screenshot Shot { get; } = shot;
    public BitmapImage Thumb { get; } = thumb;
    public string Caption => $"{Shot.InstanceName} · {Shot.Taken:dd.MM.yyyy HH:mm}";
}

/// <summary>«Профиль» → «Скриншоты»: галерея всех версий, открыть, удалить, «Поделиться» (карточка 1080×1920).</summary>
public partial class ScreenshotsViewModel(
    InstanceService instances,
    GameAuthService accounts,
    ToastService toasts,
    ILogger<ScreenshotsViewModel> log) : ObservableObject
{
    public ObservableCollection<ScreenshotItem> Items { get; } = new();
    [ObservableProperty] private ScreenshotItem? _selected;
    [ObservableProperty] private BitmapImage? _preview;
    [ObservableProperty] private string? _lastCard;
    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private bool _isLoading;

    public async Task LoadAsync()
    {
        IsLoading = true;
        var list = await Task.Run(() => ShareCardService.List(instances.List()).Take(200).Select(s =>
        {
            try { return new ScreenshotItem(s, ShareCardService.LoadThumb(s.Path, 320)); }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { return null; }
        }).OfType<ScreenshotItem>().ToList());
        IsLoading = false;
        Items.ReplaceWith(list);
        IsEmpty = Items.Count == 0;
        Selected = Items.FirstOrDefault();
    }

    partial void OnSelectedChanged(ScreenshotItem? value)
    {
        LastCard = null;
        try { Preview = value is null ? null : ShareCardService.LoadThumb(value.Shot.Path, 1280); }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { Preview = null; }
    }

    [RelayCommand]
    private void Open()
    {
        if (Selected is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Selected.Shot.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Selected is not null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Selected.Shot.Path}\"");
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } item) return;
        if (MessageBox.Show(L.T("Удалить скриншот? Он попадёт в корзину."), "FrogLauncher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Shot.Path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        var i = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.ElementAtOrDefault(Math.Min(i, Items.Count - 1));
        IsEmpty = Items.Count == 0;
    }

    /// <summary>Карточка 1080×1920: сохраняем в «Изображения\FrogLauncher», копируем в буфер, показываем в проводнике.</summary>
    [RelayCommand]
    private void Share()
    {
        if (Selected is not { } item) return;
        try
        {
            var accent = (Application.Current.Resources["AccentBrush"] as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "#22C55E";
            var file = ShareCardService.Render(item.Shot, accounts.Selected?.Name ?? L.T("Игрок"), accent);
            LastCard = file;
            try { Clipboard.SetImage(ShareCardService.LoadThumb(file, ShareCardService.Width)); }
            catch (System.Runtime.InteropServices.COMException) { }
            toasts.Show(L.T("Карточка готова и скопирована 🐸 Файл — в «Изображения\\FrogLauncher»"), ToastKind.Success, 6);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            log.LogWarning(ex, "Карточка не создана");
            toasts.Show(L.T("Не удалось сделать карточку: ") + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenCard()
    {
        if (LastCard is not null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LastCard}\"");
    }

    /// <summary>
    /// TikTok: прямой постинг через API требует одобренного TikTok-приложения разработчика, поэтому открываем
    /// страницу загрузки и папку с готовой карточкой — перетащите файл в окно браузера.
    /// </summary>
    [RelayCommand]
    private void OpenTikTok()
    {
        Shell.Open("https://www.tiktok.com/upload");
        OpenCard();
    }
}

public partial class WorldItem(WorldInfo info, BitmapImage? icon) : ObservableObject
{
    public WorldInfo Info { get; } = info;
    public BitmapImage? Icon { get; } = icon;
    public ObservableCollection<WorldBackup> Backups { get; } = new(info.Backups);
    [ObservableProperty] private WorldBackup? _selectedBackup = info.Backups.FirstOrDefault();
}

/// <summary>Настройки версии → «Миры»: список, бэкап, восстановление, экспорт, удаление.</summary>
public partial class WorldsViewModel(Instance inst, WorldService worlds, LaunchService launcher, ToastService toasts) : ObservableObject
{
    public ObservableCollection<WorldItem> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Размер папок миров считается обходом файлов — читаем в фоне.</summary>
    public async Task LoadAsync()
    {
        var list = await Task.Run(() => worlds.List(inst).Select(w =>
        {
            BitmapImage? icon = null;
            try { if (w.IconPath is not null) icon = ShareCardService.LoadThumb(w.IconPath, 64); }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { }
            return new WorldItem(w, icon);
        }).ToList());
        Items.ReplaceWith(list);
        IsEmpty = Items.Count == 0;
    }

    private bool GameRunningHere => launcher.Current is { Process.HasExited: false } s && s.Instance.Id == inst.Id;

    [RelayCommand]
    private async Task BackupAsync(WorldItem? w)
    {
        if (w is null || IsBusy) return;
        IsBusy = true;
        try
        {
            if (GameRunningHere) toasts.Show(L.T("Игра запущена — для надёжной копии лучше выйти из мира"), ToastKind.Warning);
            await Task.Run(() => worlds.Backup(inst, w.Info.Folder));
            toasts.Show(L.T($"Бэкап «{w.Info.Name}» готов 🐸"), ToastKind.Success);
            await LoadAsync();
        }
        catch (IOException ex) { toasts.Show(L.T("Бэкап не удался: ") + ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync(WorldItem? w)
    {
        if (w?.SelectedBackup is not { } b || IsBusy) return;
        if (GameRunningHere)
        {
            toasts.Show(L.T("Сначала закройте игру — мир сейчас открыт"), ToastKind.Warning);
            return;
        }
        if (MessageBox.Show(L.T($"Восстановить «{w.Info.Name}» из копии {b.Title}?\nТекущее состояние мира тоже сохранится отдельной копией."),
                "FrogLauncher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => worlds.Restore(inst, w.Info.Folder, b));
            toasts.Show(L.T("Мир восстановлен"), ToastKind.Success);
            await LoadAsync();
        }
        catch (IOException ex) { toasts.Show(L.T("Не удалось восстановить: ") + ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportAsync(WorldItem? w)
    {
        if (w is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{w.Info.Name}.zip", Filter = L.T("Архив мира|*.zip") };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => worlds.Export(inst, w.Info.Folder, dlg.FileName));
            toasts.Show(L.T("Мир экспортирован — его можно отправить другу"), ToastKind.Success);
        }
        catch (IOException ex) { toasts.Show(L.T("Экспорт не удался: ") + ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Delete(WorldItem? w)
    {
        if (w is null) return;
        if (GameRunningHere)
        {
            toasts.Show(L.T("Сначала закройте игру"), ToastKind.Warning);
            return;
        }
        if (MessageBox.Show(L.T($"Удалить мир «{w.Info.Name}»?\nОн попадёт в корзину Windows, бэкапы останутся."), "FrogLauncher",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        worlds.Delete(inst, w.Info.Folder);
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task ImportWorldAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Архив мира|*.zip"), Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) await ImportFileAsync(f);
    }

    public async Task ImportFileAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var folder = await Task.Run(() => worlds.Import(inst, path));
            toasts.Show(L.T($"Мир «{folder}» добавлен 🌍"), ToastKind.Success);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ModsException or UnauthorizedAccessException)
        {
            toasts.Show(L.T("Не удалось импортировать мир: ") + ex.Message, ToastKind.Error, 7);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenFolder(WorldItem? w)
    {
        if (w is not null) Shell.Open(Path.Combine(inst.GameDirectory, "saves", w.Info.Folder));
    }
}
