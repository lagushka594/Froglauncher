using System.Text.Json;


namespace Frog.Shared;

/// <summary>
/// Каталог тиров из tiers.json. Единственный источник цен (server-side).
/// Ссылки на другие тиры в entitlements ("frog_plus") разворачиваются рекурсивно.
/// </summary>
public sealed class TierCatalog
{
    public const decimal MinMonthPrice = 50m;
    public const decimal MaxMonthPrice = 700m;
    public const int YearMonthsBilled = 10;

    private readonly Dictionary<string, TierDto> _bySlug;
    public IReadOnlyList<TierDto> All { get; }

    /// <summary>То, что видит пользователь в GET /tiers: без скрытых тиров (admin).</summary>
    public IReadOnlyList<TierDto> Public { get; }

    private TierCatalog(IReadOnlyList<TierDto> tiers)
    {
        All = tiers;
        Public = tiers.Where(t => !t.Hidden).ToList();
        _bySlug = tiers.ToDictionary(t => t.Slug, StringComparer.Ordinal);
    }

    public static TierCatalog LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static TierCatalog Parse(string json)
    {
        var file = JsonSerializer.Deserialize<TierFile>(json, FrogJson.Options)
                   ?? throw new InvalidOperationException("tiers.json пуст");
        var raw = file.Tiers.ToDictionary(t => t.Slug, StringComparer.Ordinal);

        var expanded = new List<TierDto>();
        for (var i = 0; i < file.Tiers.Count; i++)
        {
            var t = file.Tiers[i];
            var ents = Expand(t.Slug, raw, new HashSet<string>()).Distinct().ToList();
            expanded.Add(new TierDto(t.Slug, t.Name, t.PriceMonth, t.PriceYear, t.Color, ents,
                t.Hidden, t.Highlighted, Rank: i));
        }

        Validate(expanded);
        return new TierCatalog(expanded);
    }

    public TierDto? Get(string slug) => _bySlug.GetValueOrDefault(slug);

    public TierDto Tadpole => _bySlug[TierSlugs.Tadpole];

    /// <summary>Тир можно купить: не скрыт и имеет цену.</summary>
    public bool IsPurchasable(string slug) =>
        Get(slug) is { Hidden: false, PriceMonth: > 0 };

    public decimal PriceFor(string slug, BillingPeriod period)
    {
        var t = Get(slug) ?? throw new ArgumentException($"Неизвестный тир {slug}");
        var price = period == BillingPeriod.Year ? t.PriceYear : t.PriceMonth;
        return price ?? throw new InvalidOperationException($"Тир {slug} не продаётся");
    }

    public int RankOf(string slug) => Get(slug)?.Rank ?? 0;

    private static IEnumerable<string> Expand(string slug, Dictionary<string, TierRaw> raw, HashSet<string> visiting)
    {
        if (!visiting.Add(slug))
            throw new InvalidOperationException($"Циклическая ссылка тиров: {slug}");

        foreach (var e in raw[slug].Entitlements)
        {
            if (raw.ContainsKey(e))
                foreach (var inner in Expand(e, raw, visiting)) yield return inner;
            else
                yield return e;
        }

        visiting.Remove(slug);
    }

    /// <summary>Защита от опечаток в ценах: падаем при старте, а не при оплате.</summary>
    private static void Validate(IReadOnlyList<TierDto> tiers)
    {
        if (!tiers.Any(t => t.Slug == TierSlugs.Tadpole))
            throw new InvalidOperationException("В tiers.json нет бесплатного тира tadpole");

        var admin = tiers.FirstOrDefault(t => t.Slug == TierSlugs.Admin);
        if (admin is not null && (!admin.Hidden || admin.PriceMonth is not null))
            throw new InvalidOperationException("Тир admin должен быть hidden и без цены");

        foreach (var t in tiers.Where(t => t.PriceMonth > 0))
        {
            if (t.PriceMonth < MinMonthPrice || t.PriceMonth > MaxMonthPrice)
                throw new InvalidOperationException($"{t.Slug}: цена {t.PriceMonth} ₽/мес вне диапазона 50–700");
            if (t.PriceYear != t.PriceMonth * YearMonthsBilled)
                throw new InvalidOperationException($"{t.Slug}: годовая цена должна быть = 10 × месячной");
        }
    }

    private sealed record TierFile(List<TierRaw> Tiers);

    private sealed record TierRaw(
        string Slug, string Name, decimal? PriceMonth, decimal? PriceYear, string Color,
        List<string> Entitlements, bool Hidden = false, bool Highlighted = false);
}
