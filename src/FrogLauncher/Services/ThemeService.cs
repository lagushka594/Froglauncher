using System.Windows;
using System.Windows.Media;
using Frog.Shared;

namespace FrogLauncher.Services;

public sealed record ThemeInfo(string Key, string Name, string Accent, string Accent2, string Surface, string? Requires)
{
    public string RequiresLabel => Requires switch
    {
        null => "Бесплатно",
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
        new ThemeInfo("swamp", "Болото", "#22C55E", "#06B6D4", "#0B1220", null),
        new ThemeInfo("lily", "Кувшинка", "#EC4899", "#22C55E", "#120B1A", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("night", "Ночной пруд", "#3B82F6", "#8B5CF6", "#070B18", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("firefly", "Светлячки", "#FACC15", "#22C55E", "#0D1208", EntitlementKeys.PremiumThemes3),
        new ThemeInfo("sunset", "Закат", "#F97316", "#F43F5E", "#160C0A", EntitlementKeys.PremiumThemesAll),
        new ThemeInfo("mint", "Мята", "#2DD4BF", "#A3E635", "#071311", EntitlementKeys.PremiumThemesAll),
        new ThemeInfo("legend", "Легенда", "#8B5CF6", "#F59E0B", "#0E0718", EntitlementKeys.ExclusiveTheme),
    };

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
        var theme = Themes.FirstOrDefault(t => t.Key == key) is { } t && IsUnlocked(t) ? t : Themes[0];
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
