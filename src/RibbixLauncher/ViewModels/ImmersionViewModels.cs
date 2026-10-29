using RibbixLauncher.Core;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

/// <summary>Столбик графика активности.</summary>
public sealed record DayBar(string Label, string Tooltip, double Height, bool IsToday);

public sealed record StatTile(string Value, string Label);

public sealed record VersionTile(string Name, string Time, string Sessions);

/// <summary>Строка списка достижений: прогресс по группам требований, секретные — без названия, пока не выполнены.</summary>
public sealed record AdvancementItem(string Title, string Description, string Frame, string ProgressText, double Percent, bool IsDone);

public sealed record AdvancementCategory(string Key, string Title);

/// <summary>«Профиль» → «Статистика»: часы, сессии, серия дней, бейдж, график по дням, счётчики Minecraft, достижения.</summary>
public sealed record AchievementTile(string Emoji, string Title, string Description, bool Unlocked)
{
    public string Tooltip => Unlocked ? Description : L.T("🔒 ") + Description;
}

public partial class StatsViewModel(PlayStatsService stats, InstanceService instances, AdvancementService advancements, AchievementService achievements) : ObservableObject
{
    public ObservableCollection<AchievementTile> Achievements { get; } = new();
    [ObservableProperty] private string _achievementsText = "";

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
        achievements.Check();
        var list = achievements.List();
        Achievements.ReplaceWith(list.OrderByDescending(a => a.Unlocked is not null)
            .Select(a => new AchievementTile(a.Def.Emoji, a.Def.Title, a.Def.Description, a.Unlocked is not null)));
        AchievementsText = L.T($"{list.Count(a => a.Unlocked is not null)} из {list.Count}");
        var log = stats.Load();
        var sessions = log.Sessions;
        var mc = stats.ReadMinecraftStats();
        var tracked = TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks));
        // Лаунчер считает только свои сессии; время из миров Minecraft включает и игру до RibbixLauncher.
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

/// <summary>«Профиль» → «🎁 Мой год»: карточки итогов года, листание, «Поделиться» и «Сохранить все».</summary>
public partial class YearRecapViewModel(
    PlayStatsService stats,
    InstanceService instances,
    GameAuthService accounts,
    ToastService toasts,
    ILogger<YearRecapViewModel> log) : ObservableObject
{
    public ObservableCollection<int> Years { get; } = new();
    [ObservableProperty] private int _year;
    [ObservableProperty] private int _index;
    [ObservableProperty] private BitmapSource? _preview;
    [ObservableProperty] private string _position = "";
    [ObservableProperty] private string? _lastCard;
    private YearRecap? _recap;
    private PlayLog? _log;
    private McStats? _mc;

    public void Load()
    {
        _log = stats.Load();
        _mc = stats.ReadMinecraftStats();
        var now = AppClock.Now.Year;
        Years.ReplaceWith(_log.Sessions.Select(s => s.Start.LocalDateTime.Year).Append(now).Distinct().OrderByDescending(y => y));
        // В январе итоги прошлого года интереснее только начавшегося.
        var pick = AppClock.Now.Month == 1 && Years.Contains(now - 1) ? now - 1 : now;
        if (Year == pick) Build(); else Year = pick;
    }

    partial void OnYearChanged(int value) => Build();

    private void Build()
    {
        if (_log is null || _mc is null || Year == 0) return;
        var names = instances.List().ToDictionary(i => i.Id, i => i.Name);
        var shots = ShareCardService.List(instances.List()).Select(s => s.Taken);
        _recap = YearRecapService.Compute(Year, _log.Sessions, names, shots, _mc, _mc.Advancements);
        if (Index == 0) Render(); else Index = 0;
    }

    partial void OnIndexChanged(int value) => Render();

    private Color Accent => (Application.Current?.Resources["AccentBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x22, 0xC5, 0x5E);
    private string Nick => accounts.Selected?.Name ?? L.T("Игрок");

    private void Render()
    {
        if (_recap is null) return;
        LastCard = null;
        Preview = YearRecapService.Render(_recap, Index, Nick, Accent);
        Position = $"{Index + 1} / {YearRecapService.CardCount(_recap)}";
    }

    [RelayCommand]
    private void Next()
    {
        if (_recap is not null) Index = (Index + 1) % YearRecapService.CardCount(_recap);
    }

    [RelayCommand]
    private void Prev()
    {
        if (_recap is not null) Index = (Index + YearRecapService.CardCount(_recap) - 1) % YearRecapService.CardCount(_recap);
    }

    /// <summary>Текущая карточка: файл в «Изображения\RibbixLauncher\Мой год N», картинка — в буфер обмена.</summary>
    [RelayCommand]
    private void Share()
    {
        if (_recap is null || Preview is null) return;
        try
        {
            var file = YearRecapService.Save(Preview, Path.Combine(YearRecapService.OutputDir(Year), $"{Index + 1:00}.png"));
            LastCard = file;
            try { Clipboard.SetImage(Preview); }
            catch (System.Runtime.InteropServices.COMException) { }
            toasts.Show(L.T("Карточка готова и скопирована 🐸 Вставьте её в сторис или TikTok"), ToastKind.Success, 6);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Карточка года не сохранена");
            toasts.Show(L.T("Не удалось сделать карточку: ") + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void SaveAll()
    {
        if (_recap is null) return;
        try
        {
            string? first = null;
            for (var i = 0; i < YearRecapService.CardCount(_recap); i++)
                first ??= YearRecapService.Save(YearRecapService.Render(_recap, i, Nick, Accent),
                    Path.Combine(YearRecapService.OutputDir(Year), $"{i + 1:00}.png"));
            LastCard = first;
            OpenCard();
            toasts.Show(L.T("Все карточки сохранены — выкладывайте серией 🎁"), ToastKind.Success, 6);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Карточки года не сохранены");
            toasts.Show(L.T("Не удалось сделать карточку: ") + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenCard()
    {
        if (LastCard is not null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LastCard}\"");
    }

    [RelayCommand]
    private void OpenTikTok()
    {
        if (LastCard is null) Share();
        Shell.Open("https://www.tiktok.com/upload");
        OpenCard();
    }
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
        if (MessageBox.Show(L.T("Удалить скриншот? Он попадёт в корзину."), "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Shot.Path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        var i = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.ElementAtOrDefault(Math.Min(i, Items.Count - 1));
        IsEmpty = Items.Count == 0;
    }

    /// <summary>Карточка 1080×1920: сохраняем в «Изображения\RibbixLauncher», копируем в буфер, показываем в проводнике.</summary>
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
            toasts.Show(L.T("Карточка готова и скопирована 🐸 Файл — в «Изображения\\RibbixLauncher»"), ToastKind.Success, 6);
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
                "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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

    [ObservableProperty] private string? _timelapseStatus;

    /// <summary>Карта мира — отдельное окно, мир только читается.</summary>
    [RelayCommand]
    private void OpenMap(WorldItem? w)
    {
        if (w is null) return;
        QuestService.MarkGlobal("map");
        new RibbixLauncher.Views.WorldMapWindow(Path.Combine(inst.GameDirectory, "saves", w.Info.Folder), w.Info.Name)
        {
            Owner = Application.Current.MainWindow,
        }.Show();
    }

    /// <summary>Таймлапс из бэкапов: кадр на каждый бэкап + текущий мир. Рисование WPF — в отдельном STA-потоке.</summary>
    [RelayCommand]
    private async Task TimelapseAsync(WorldItem? w)
    {
        if (w is null || IsBusy) return;
        if (w.Backups.Count == 0)
        {
            toasts.Show(L.T("Нужен хотя бы один бэкап: лаунчер делает их сам раз в сутки после игры, или нажмите «Бэкап»."), ToastKind.Info, 7);
            return;
        }
        IsBusy = true;
        var outDir = Path.Combine(ShareCardService.OutputDir, L.T("Таймлапсы"), string.Concat(w.Info.Name.Split(Path.GetInvalidFileNameChars())) + $"-{DateTime.Now:yyyyMMdd-HHmm}");
        var progress = new Progress<string>(s => TimelapseStatus = s);
        try
        {
            var tcs = new TaskCompletionSource<TimelapseResult>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(TimelapseService.Create(inst, w.Info, w.Backups.ToList(), 256, outDir, progress, CancellationToken.None)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            var r = await tcs.Task;
            TimelapseStatus = L.T($"Готово: {r.Frames} кадров. GIF и PNG-кадры — в «Изображения\\RibbixLauncher\\Таймлапсы».");
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{r.GifPath}\"");
            toasts.Show(L.T("Таймлапс готов 🎞 Кадры можно склеить в CapCut для TikTok"), ToastKind.Success, 7);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TimelapseStatus = null;
            toasts.Show(L.T("Таймлапс не получился: ") + ex.Message, ToastKind.Error, 7);
        }
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
        if (MessageBox.Show(L.T($"Удалить мир «{w.Info.Name}»?\nОн попадёт в корзину Windows, бэкапы останутся."), "Ribbix Launcher",
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


public sealed partial class CaptureItem(Capture capture) : ObservableObject
{
    public Capture Capture { get; } = capture;
    public string Caption => L.T($"{Capture.Taken:dd.MM.yyyy HH:mm} · {Capture.Size / 1048576.0:0.#} МБ");
    public string Name => Path.GetFileNameWithoutExtension(Capture.Path);
}

/// <summary>«Профиль» → «Клипы»: запись Xbox Game Bar → вертикальный клип 1080×1920 для TikTok.</summary>
public partial class ClipsViewModel(ClipService clips, ToastService toasts, ILogger<ClipsViewModel> log) : ObservableObject
{
    public ObservableCollection<CaptureItem> Items { get; } = new();
    [ObservableProperty] private CaptureItem? _selected;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _lastClip;

    public bool HasFfmpeg => ClipService.HasFfmpeg;

    public void Load()
    {
        Items.ReplaceWith(ClipService.List().Take(50).Select(c => new CaptureItem(c)));
        IsEmpty = Items.Count == 0;
        Selected ??= Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasFfmpeg));
    }

    [RelayCommand]
    private void Refresh() => Load();

    [RelayCommand]
    private static void OpenGameBarSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:gaming-gamedvr") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    [RelayCommand]
    private void OpenCaptures()
    {
        Directory.CreateDirectory(ClipService.CapturesDir);
        Shell.Open(ClipService.CapturesDir);
    }

    [RelayCommand]
    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Видео") + " (*.mp4)|*.mp4", InitialDirectory = ClipService.CapturesDir };
        if (dlg.ShowDialog() != true) return;
        var f = new FileInfo(dlg.FileName);
        var item = new CaptureItem(new Capture(f.FullName, f.LastWriteTime, f.Length));
        Items.Insert(0, item);
        IsEmpty = false;
        Selected = item;
    }

    [RelayCommand]
    private async Task MakeAsync()
    {
        if (Selected is not { } item || IsWorking) return;
        IsWorking = true;
        LastClip = null;
        try
        {
            if (!ClipService.HasFfmpeg)
            {
                if (MessageBox.Show(L.T("Для клипов нужен ffmpeg (~150 МБ, один раз). Скачать его с GitHub (сборка BtbN)?"),
                        "Ribbix Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Status = L.T("Скачиваем ffmpeg…");
                await clips.EnsureFfmpegAsync(new Progress<double>(p => Percent = p), CancellationToken.None);
                OnPropertyChanged(nameof(HasFfmpeg));
            }
            Percent = 0;
            Status = L.T("Монтируем клип…");
            LastClip = await clips.MakeAsync(item.Capture, new Progress<double>(p => Percent = p), CancellationToken.None);
            QuestService.MarkGlobal("clip");
            toasts.Show(L.T("🎬 Клип готов! Файл — в «Изображения\\RibbixLauncher\\Клипы»"), ToastKind.Success, 6);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LastClip}\"");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException
                                       or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            log.LogWarning(ex, "Клип не создан");
            toasts.Show(L.T("Не удалось сделать клип: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsWorking = false;
            Status = null;
        }
    }

    [RelayCommand]
    private void OpenTikTok()
    {
        Shell.Open("https://www.tiktok.com/upload");
        if (LastClip is not null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LastClip}\"");
    }
}
