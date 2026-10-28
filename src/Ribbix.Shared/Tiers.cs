namespace Ribbix.Shared;

/// <summary>Слаги тиров. Admin не продаётся и выдаётся только через RibbixNews.</summary>
public static class TierSlugs
{
    public const string Tadpole = "tadpole";
    public const string RibbixLite = "ribbix_lite";
    public const string RibbixPlus = "ribbix_plus";
    public const string RibbixPro = "ribbix_pro";
    public const string RibbixLegend = "ribbix_legend";
    public const string Admin = "admin";
}

/// <summary>Ключи фич (entitlements). Значения совпадают с tiers.json.</summary>
public static class EntitlementKeys
{
    public const string Base = "base";
    public const string NoAds = "no_ads";
    public const string CloudSync = "cloud_sync";
    public const string PremiumThemes3 = "premium_themes_3";
    public const string PremiumThemesAll = "premium_themes_all";
    /// <summary>Устаревший ключ (лимит 5) — оставлен для совместимости со старыми токенами.</summary>
    public const string Instances5 = "instances_5";
    public const string Instances50 = "instances_50";
    public const string InstancesUnlimited = "instances_unlimited";
    public const string CloudBackup10Gb = "cloud_backup_10gb";
    public const string CloudBackup50Gb = "cloud_backup_50gb";
    public const string JvmAutotune = "jvm_autotune";
    public const string PriorityDownload = "priority_download";
    public const string CrashAnalytics = "crash_analytics";
    public const string EarlyAccess = "early_access";
    public const string PersonalSupport = "personal_support";
    public const string RoadmapVote = "roadmap_vote";
    public const string ExclusiveTheme = "exclusive_theme";
    public const string AdminBadge = "admin_badge";
    public const string NewsPublish = "news_publish";
    public const string UserManagement = "user_management";
    public const string BetaChannel = "beta_channel";

    /// <summary>Сколько версий (инстансов) можно создать бесплатно.</summary>
    public const int FreeInstances = 25;

    /// <summary>Лимит версий по набору фич: Tadpole — 25, RibbixLite — 50, Ribbix+ и выше — null (без ограничений).</summary>
    public static int? MaxInstances(IReadOnlyCollection<string> entitlements)
    {
        if (entitlements.Contains(InstancesUnlimited)) return null;
        if (entitlements.Contains(Instances50)) return 50;
        return FreeInstances;
    }
}

public enum BillingPeriod { Month, Year }

/// <summary>Тир в том виде, в каком его отдаёт GET /tiers (entitlements уже развёрнуты).</summary>
public sealed record TierDto(
    string Slug,
    string Name,
    decimal? PriceMonth,
    decimal? PriceYear,
    string Color,
    IReadOnlyList<string> Entitlements,
    bool Hidden = false,
    bool Highlighted = false,
    int Rank = 0);

public sealed record TiersResponse(IReadOnlyList<TierDto> Tiers);

/// <summary>Ответ GET /entitlements.</summary>
public sealed record EntitlementsDto(
    string Tier,
    IReadOnlyList<string> Entitlements,
    DateTimeOffset? PeriodEnd,
    bool InGrace,
    DateTimeOffset? GraceUntil,
    bool CancelAtPeriodEnd = false);
