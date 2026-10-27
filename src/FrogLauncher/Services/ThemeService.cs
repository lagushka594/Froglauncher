using FrogLauncher.Core;
using System.Windows;
using System.Windows.Media;
using Frog.Shared;

namespace FrogLauncher.Services;

public sealed record ThemeInfo(string Key, string Name, string Accent, string Accent2, string Surface, string? Requires)
{
    public string RequiresLabel => Requires switch
    {
        null => L.T("Бесплатно"),
        EntitlementKeys.PremiumThemes3 => "Froglet+",
        EntitlementKeys.PremiumThemesAll => "Frog+",
        EntitlementKeys.ExclusiveTheme => "Frog Legend",
        _ => "",
    };
}

/// <summary>Темы оформления. Цвета — DynamicResource, меняются на лету без перезапуска.</summary>
public sealed class ThemeService(EntitlementService entitlements)
{
    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("swamp", L.T("Болото"), "#22C55E", "#06B6D4", "#0B1220", null),
        new ThemeInfo("lily", L.T("Кувшинка"), "#EC4899", "#22C55E", "#120B1A", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("night", L.T("Ночной пруд"), "#3B82F6", "#8B5CF6", "#070B18", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("firefly", L.T("Светлячки"), "#FACC15", "#22C55E", "#0D1208", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("sunset", L.T("Закат"), "#F97316", "#F43F5E", "#160C0A", EntitlementKeys.PremiumThemesAll),
        new ThemeInfo("mint", L.T("Мята"), "#2DD4BF", "#A3E635", "#071311", EntitlementKeys.PremiumThemesAll),
        new ThemeInfo("legend", L.T("Легенда"), "#8B5CF6", "#F59E0B", "#0E0718", EntitlementKeys.ExclusiveTheme),
        // Сезонные — бесплатные: «Сезонная» сама переключается по дате, остальные можно выбрать вручную.
        new ThemeInfo(Seasons.AutoKey, L.T("🗓 Сезонная (авто)"), "#22C55E", "#F472B6", "#0B1220", null),
        new ThemeInfo("season-spring", L.T("🌸 Цветение"), "#4ADE80", "#F472B6", "#0F1412", null),
        new ThemeInfo("season-summer", L.T("☀️ Пруд"), "#10B981", "#FACC15", "#06140F", null),
        new ThemeInfo("season-autumn", L.T("🍂 Листопад"), "#F59E0B", "#DC2626", "#140C06", null),
        new ThemeInfo("season-winter", L.T("❄️ Спящий пруд"), "#7DD3FC", "#E0F2FE", "#07101A", null),
        new ThemeInfo("season-newyear", L.T("🎄 Ква-Новый год"), "#DC2626", "#16A34A", "#120808", null),
    };

    /// <summary>Сезон активной темы (эффекты, звук, ёлка); null — обычная тема без сезонных эффектов.</summary>
    public Season? ActiveSeason { get; private set; }
    public event Action? SeasonChanged;

    /// <summary>Для «Сезонной (авто)» — тема текущего сезона по дате.</summary>
    public static ThemeInfo Resolve(string key, DateOnly today) =>
        Themes.First(t => t.Key == (key == Seasons.AutoKey ? Seasons.KeyOf(Seasons.For(today)) : key));

    public bool IsUnlocked(ThemeInfo t) => t.Requires switch
    {
        null => true,
        EntitlementKeys.PremiumThemes3 => entitlements.IsAllowed(EntitlementKeys.PremiumThemes3) ||
                                          entitlements.IsAllowed(EntitlementKeys.PremiumThemesAll),
        var key => entitlements.IsAllowed(key),
    };

    /// <summary>Применяет тему; недоступная (подписка кончилась) откатывается к «Болоту».</summary>
    public ThemeInfo Apply(string key)
    {
        var chosen = Themes.FirstOrDefault(t => t.Key == key) is { } t && IsUnlocked(t) ? t : Themes[0];
        var theme = Resolve(chosen.Key, AppClock.Today);
        var season = Seasons.FromKey(theme.Key);
        if (season != ActiveSeason)
        {
            ActiveSeason = season;
            SeasonChanged?.Invoke();
        }
        var res = Application.Current.Resources;
        res["AccentBrush"] = Brush(theme.Accent);
        res["Accent2Brush"] = Brush(theme.Accent2);
        res["SurfaceBrush"] = Brush(theme.Surface);
        res["AccentColor"] = (Color)ColorConverter.ConvertFromString(theme.Accent);
        res["AccentGradient"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(theme.Accent),
            (Color)ColorConverter.ConvertFromString(theme.Accent2), 0);
        return theme;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
