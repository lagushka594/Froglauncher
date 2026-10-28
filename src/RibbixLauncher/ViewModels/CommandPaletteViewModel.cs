using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;

namespace RibbixLauncher.ViewModels;

public sealed record PaletteItem(string Icon, string Title, string Category, Action Run, string? Subtitle = null, string Keywords = "")
{
    public const string Act = "Действие", PageKind = "Страница", Setting = "Настройка", Version = "Версия",
        Minecraft = "Minecraft", Mod = "Мод", Search = "Поиск";

    /// <summary>Category — внутренний ключ, на экран — перевод.</summary>
    public string CategoryText => L.T(Category);

    /// <summary>Очки совпадения с запросом — для сортировки.</summary>
    public int Score { get; init; }
}

/// <summary>
/// Нечёткий поиск как в VS Code: символы запроса идут в тексте по порядку, не обязательно подряд.
/// Бонусы — за совпадение подряд, с начала слова и с начала строки. Регистр и «ё/е» не важны.
/// </summary>
public static class Fuzzy
{
    private const string En = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Ru = "йцукенгшщзхъфывапролджэячсмитьбюё";

    /// <summary>null — не подходит; больше — лучше.</summary>
    public static int? Score(string query, string text)
    {
        query = Norm(query);
        if (query.Length == 0) return 0;
        var t = Norm(text);
        int score = 0, ti = 0, run = 0;
        foreach (var c in query)
        {
            if (c == ' ') { run = 0; continue; }
            var found = t.IndexOf(c, ti);
            if (found < 0) return null;
            var wordStart = found == 0 || !char.IsLetterOrDigit(t[found - 1]);
            run = found == ti && ti > 0 ? run + 1 : 0;
            score += 1 + run * 5 + (wordStart ? 8 : 0) + (found == 0 ? 6 : 0) - Math.Min(found - ti, 6);
            ti = found + 1;
        }
        if (t.StartsWith(query, StringComparison.Ordinal)) score += 20;
        return score;
    }

    /// <summary>Лучший результат из запроса как есть и запроса, набранного в другой раскладке («yfcnhjqrb» → «настройки»).</summary>
    public static int? Best(string query, string text)
    {
        var a = Score(query, text);
        var b = Score(SwapLayout(query), text);
        return a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }

    public static string SwapLayout(string s) => new(s.ToLowerInvariant().Select(c =>
    {
        var i = En.IndexOf(c);
        if (i >= 0) return Ru[i];
        i = Ru.IndexOf(c);
        return i >= 0 ? En[i] : c;
    }).ToArray());

    private static string Norm(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е');
}

/// <summary>
/// Командная палитра (Ctrl+K): страницы, действия, настройки, версии игрока, версии Minecraft и моды Modrinth.
/// Стрелки — выбор, Enter — выполнить, Esc — закрыть.
/// </summary>
public partial class CommandPaletteViewModel(
    MainViewModel main,
    InstanceService instances,
    VersionService versions,
    ModrinthService modrinth) : ObservableObject
{
    public const int MaxResults = 12;

    public ObservableCollection<PaletteItem> Results { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;
    [ObservableProperty] private bool _isSearchingMods;

    private List<PaletteItem> _static = new();
    private IReadOnlyList<ManifestEntry> _mcVersions = Array.Empty<ManifestEntry>();
    private List<PaletteItem> _mods = new();
    private CancellationTokenSource? _modCts;

    public void Open()
    {
        _static = BuildStatic();
        _mods.Clear();
        Query = "";
        Filter();
        IsOpen = true;
        _ = LoadVersionsAsync();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _modCts?.Cancel();
    }

    [RelayCommand]
    private void Execute(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Close();
        item.Run();
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[(i + delta + Results.Count) % Results.Count];
    }

    partial void OnQueryChanged(string value)
    {
        _mods.Clear();
        Filter();
        _ = SearchModsAsync(value);
    }

    private void Filter()
    {
        var q = Query.Trim();
        IEnumerable<PaletteItem> pool = _static.Concat(_mods);
        if (q.Length > 0) pool = pool.Concat(VersionItems(q)).Concat(SearchActions(q));

        var scored = q.Length == 0
            ? pool.Where(i => i.Category is PaletteItem.Act or PaletteItem.PageKind)
            : pool.Select(i => i with { Score = Math.Max(Fuzzy.Best(q, i.Title) ?? -1, (Fuzzy.Best(q, i.Keywords) ?? 0) - 5) })
                .Where(i => i.Score >= 0)
                // Действия «Искать …» всегда в конце.
                .OrderByDescending(i => i.Category == PaletteItem.Search ? int.MinValue : i.Score);

        Results.ReplaceWith(scored.Take(MaxResults));
        Selected = Results.FirstOrDefault();
    }

    private List<PaletteItem> BuildStatic()
    {
        var list = new List<PaletteItem>
        {
            new("▶", L.T("Играть"), PaletteItem.Act, () => main.RunHotkey("play"), L.T("Запустить выбранную версию"), L.T("запуск start play")),
            new("■", L.T("Остановить игру"), PaletteItem.Act, main.StopGame, null, L.T("стоп закрыть stop kill")),
            new("➕", L.T("Новая версия"), PaletteItem.Act, () => main.RunHotkey("new"), L.T("Создать версию Minecraft"), L.T("создать добавить new")),
            new("⟳", L.T("Обновить страницу"), PaletteItem.Act, () => main.RunHotkey("refresh"), "F5", "refresh"),
            new("⬆", L.T("Проверить обновления Ribbix"), PaletteItem.Act, () => main.Go<SettingsViewModel>(Page.Settings, s => s.CheckUpdateCommand.Execute(null)), null, L.T("update апдейт")),
            new("📁", L.T("Открыть папку данных"), PaletteItem.Act, () => Shell.Open(LauncherPaths.Root), "%AppData%\\RibbixLauncher", "folder appdata"),
            new("🔎", L.T("Поиск на странице"), PaletteItem.Act, main.RequestPageSearch, L.T("Поле поиска текущей страницы"), "find"),
            new("🐸", L.T("Пройти знакомство заново"), PaletteItem.Act, () => main.Go<AboutViewModel>(Page.Help, a => a.ShowOnboardingCommand.Execute(null)), null, L.T("онбординг onboarding")),
            new("🎟", L.T("Ввести промокод"), PaletteItem.Act, () => main.Go<SubscriptionViewModel>(Page.Subscription, s => s.ShowPromoBox = true), null, L.T("скидка promo купон")),
        };

        if (Seasons.For(AppClock.Today) == Season.NewYear)
        {
            list.Add(new PaletteItem("🎆", L.T("Запустить фейерверк"), PaletteItem.Act, main.ShowFireworks, null, L.T("новый год салют")));
            list.Add(new PaletteItem("🎁", L.T("Новогодний подарок дня"), PaletteItem.Act, () => main.SelectedPage = Page.Home, null, L.T("купон скидка ёлка")));
        }

        foreach (var (page, icon, title, keys) in new[]
                 {
                     (Page.Home, "🏠", L.T("Главная"), "home"),
                     (Page.Catalog, "📂", L.T("Каталог версий"), L.T("версии инстансы catalog")),
                     (Page.Modpacks, "📦", L.T("Сборки Modrinth"), L.T("модпаки modpacks")),
                     (Page.Servers, "🌐", L.T("Серверы"), L.T("servers мультиплеер")),
                     (Page.Java, "☕", "Java", L.T("джава jre jdk")),
                     (Page.Settings, "⚙", L.T("Настройки"), L.T("settings параметры")),
                     (Page.Profile, "👤", L.T("Профиль и аккаунты"), L.T("аккаунт ник discord microsoft")),
                     (Page.Subscription, "👑", L.T("Подписка"), L.T("тариф premium купить")),
                     (Page.Help, "❓", L.T("Помощь"), L.T("about о программе")),
                 })
            list.Add(new PaletteItem(icon, title, PaletteItem.PageKind, () => main.SelectedPage = page, null, keys));

        foreach (var (title, keys) in new[]
                 {
                     (L.T("Тема оформления"), L.T("цвет theme")), (L.T("Язык интерфейса"), L.T("language локализация english")),
                     (L.T("Горячие клавиши"), L.T("hotkeys сочетания")), (L.T("Трей и напоминания"), L.T("tray перерыв")),
                     (L.T("Автообновление и бета-канал"), "update beta"), (L.T("Активность в Discord"), "rich presence"),
                     (L.T("RAM по умолчанию"), L.T("память озу")),
                 })
            list.Add(new PaletteItem("⚙", title, PaletteItem.Setting, () => main.SelectedPage = Page.Settings, L.T("Настройки"), keys));

        foreach (var (tab, icon, title, keys) in new[]
                 {
                     (1, "📊", L.T("Статистика игры"), L.T("часы бейдж stats")), (2, "🖼", L.T("Скриншоты"), L.T("screenshots поделиться tiktok")),
                     (ProfileViewModel.RecapTab, "🎁", L.T("Мой год в Minecraft"), L.T("итоги года wrapped recap")),
                 })
            list.Add(new PaletteItem(icon, title, PaletteItem.PageKind, () => main.Go<ProfileViewModel>(Page.Profile, p => p.Tab = tab), L.T("Профиль"), keys));

        foreach (var inst in instances.List())
        {
            var sub = $"{inst.GameVersion} · {inst.Loader}";
            list.Add(new PaletteItem("▶", L.T($"Играть: {inst.Name}"), PaletteItem.Version, () => main.PlayInstance(inst.Id), sub, inst.GameVersion));
            list.Add(new PaletteItem("🛠", L.T($"Настройки: {inst.Name}"), PaletteItem.Version, () => main.Go<InstancesViewModel>(Page.Catalog, c => c.SelectById(inst.Id)), sub, L.T("моды миры ") + inst.GameVersion));
        }
        return list;
    }

    /// <summary>Версии Minecraft показываем только по запросу: их сотни.</summary>
    private IEnumerable<PaletteItem> VersionItems(string q)
    {
        if (!q.Any(char.IsDigit)) return Array.Empty<PaletteItem>();
        return _mcVersions
            .Where(v => v.Type == "release" || q.Length >= 4)
            .Where(v => v.Id.StartsWith(q.TrimStart('m', 'c', ' '), StringComparison.OrdinalIgnoreCase) || q.Contains(v.Id))
            .Take(5)
            .Select(v => new PaletteItem("⛏", L.T($"Создать версию Minecraft {v.Id}"), PaletteItem.Minecraft,
                () => main.Go<InstancesViewModel>(Page.Catalog, c => c.StartCreateWith(v)), v.Type == "release" ? L.T("релиз") : v.Type, v.Id));
    }

    /// <summary>«Искать …» — всегда последними: если ничего не подошло, Enter всё равно полезен.</summary>
    private IEnumerable<PaletteItem> SearchActions(string q)
    {
        yield return new PaletteItem("📦", L.T($"Искать «{q}» в сборках Modrinth"), PaletteItem.Search,
            () => main.Go<ModpacksViewModel>(Page.Modpacks, m => m.SearchFor(q)));
        if (main.HomeInstance is { Loader: not LoaderType.Vanilla } inst)
            yield return new PaletteItem("🧩", L.T($"Искать мод «{q}» для «{inst.Name}»"), PaletteItem.Search,
                () => main.Go<InstancesViewModel>(Page.Catalog, c => c.OpenModSearch(inst.Id, q)));
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            _mcVersions = (await versions.GetManifestAsync()).Versions;
            if (Query.Length > 0) Filter();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Моды Modrinth для выбранной на главной версии (если у неё есть модлоадер).</summary>
    private async Task SearchModsAsync(string q)
    {
        _modCts?.Cancel();
        if (q.Trim().Length < 3 || main.HomeInstance is not { Loader: not LoaderType.Vanilla } inst) return;
        var cts = _modCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            IsSearchingMods = true;
            var hits = await modrinth.SearchAsync(inst, q, 0, cts.Token);
            if (cts.IsCancellationRequested) return;
            _mods = hits.Take(5).Select(h => new PaletteItem("🧩", h.Title, PaletteItem.Mod,
                () => main.Go<InstancesViewModel>(Page.Catalog, c => c.OpenModSearch(inst.Id, h.Title)),
                L.T($"Мод для «{inst.Name}» · {h.Downloads:N0} загрузок"), h.Slug + " " + h.Description)).ToList();
            Filter();
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or ModsException) { }
        finally
        {
            if (_modCts == cts) IsSearchingMods = false;
        }
    }
}
