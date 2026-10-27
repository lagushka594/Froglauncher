using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Frog.Shared;

/// <summary>Акция по расписанию (повторяется каждый год).</summary>
public sealed class PromoEvent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Emoji { get; set; } = "";
    public int Percent { get; set; }
    /// <summary>"MM-dd" — начало (включительно). Для плавающих дат — <see cref="Rule"/>.</summary>
    public string? From { get; set; }
    /// <summary>"MM-dd" — конец (включительно). Может быть «через Новый год»: 12-20 … 01-10.</summary>
    public string? To { get; set; }
    /// <summary>"black-friday" — пятница после 4-го четверга ноября, до Киберпонедельника.</summary>
    public string? Rule { get; set; }
    /// <summary>Пусто — на все платные тарифы.</summary>
    public List<string> Tiers { get; set; } = new();
}

/// <summary>Промокод (в т.ч. партнёрский код стримера).</summary>
public sealed class PromoCode
{
    /// <summary>Код открытым текстом — или <see cref="Hash"/>, чтобы его нельзя было подсмотреть в promos.json.</summary>
    public string? Code { get; set; }
    /// <summary>SHA-256 (hex) от кода в ВЕРХНЕМ регистре.</summary>
    public string? Hash { get; set; }
    public int Percent { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public List<string> Tiers { get; set; } = new();
    /// <summary>"month" / "year" / пусто — любой период.</summary>
    public string? Period { get; set; }
    /// <summary>Имя партнёра (стримера) — попадает в заказ и статистику.</summary>
    public string? Partner { get; set; }
}

public sealed class PromoFile
{
    public int Format { get; set; } = 1;
    public List<PromoEvent> Events { get; set; } = new();
    public List<PromoCode> Codes { get; set; } = new();
}

/// <summary>Итоговая скидка для тарифа: процент, откуда она, и цена после неё.</summary>
/// <param name="Label">Для акции — её название (переводится в лаунчере), для промокода — сам код.</param>
public sealed record Discount(int Percent, string Source, string Label, string? Code, string? Partner, string Emoji = "")
{
    public decimal Apply(decimal price) => Math.Round(price * (100 - Percent) / 100m, 0, MidpointRounding.AwayFromZero);
}

public enum PromoCheck { Ok, Unknown, Expired, NotYet, WrongTier, WrongPeriod }

/// <summary>
/// Промокоды и сезонные скидки. Одна и та же логика в лаунчере (показ цены и заказ в Discord) и в API (сумма к оплате).
/// Скидки не суммируются — действует большая из акции и промокода.
/// </summary>
public sealed class PromoCatalog(PromoFile file)
{
    public PromoFile File { get; } = file;

    public static PromoCatalog Parse(string json) =>
        new(JsonSerializer.Deserialize<PromoFile>(json, FrogJson.Options) ?? new PromoFile());

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    /// <summary>Акции, идущие в этот день (по местной дате игрока / сервера).</summary>
    public IEnumerable<PromoEvent> ActiveEvents(DateOnly today) => File.Events.Where(e => IsActive(e, today));

    public static bool IsActive(PromoEvent e, DateOnly today)
    {
        if (e.Rule == "black-friday")
        {
            var start = BlackFriday(today.Year);
            return today >= start && today <= start.AddDays(3);
        }
        if (!TryMonthDay(e.From, today.Year, out var from) || !TryMonthDay(e.To, today.Year, out var to)) return false;
        // Через Новый год: 12-20 … 01-10.
        return from <= to ? today >= from && today <= to : today >= from || today <= to;
    }

    /// <summary>Пятница после 4-го четверга ноября (День благодарения в США).</summary>
    public static DateOnly BlackFriday(int year)
    {
        var d = new DateOnly(year, 11, 1);
        while (d.DayOfWeek != DayOfWeek.Thursday) d = d.AddDays(1);
        return d.AddDays(21 + 1);
    }

    private static bool TryMonthDay(string? s, int year, out DateOnly date)
    {
        date = default;
        if (s is null || !DateTime.TryParseExact(s, "MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var md)) return false;
        date = new DateOnly(year, md.Month, Math.Min(md.Day, DateTime.DaysInMonth(year, md.Month)));
        return true;
    }

    public PromoCode? Find(string code)
    {
        var norm = Normalize(code);
        if (norm.Length == 0) return null;
        var hash = HashCode(norm);
        return File.Codes.FirstOrDefault(c =>
            (c.Code is not null && Normalize(c.Code) == norm) ||
            (c.Hash is not null && string.Equals(c.Hash, hash, StringComparison.OrdinalIgnoreCase)));
    }

    public PromoCheck Check(string code, string tier, BillingPeriod period, DateTimeOffset now, out PromoCode? promo)
    {
        promo = Find(code);
        if (promo is null) return PromoCheck.Unknown;
        if (promo.ValidUntil is { } until && now > until) return PromoCheck.Expired;
        if (promo.ValidFrom is { } from && now < from) return PromoCheck.NotYet;
        if (promo.Tiers.Count > 0 && !promo.Tiers.Contains(tier)) return PromoCheck.WrongTier;
        if (promo.Period is { Length: > 0 } p && !string.Equals(p, period == BillingPeriod.Year ? "year" : "month", StringComparison.OrdinalIgnoreCase))
            return PromoCheck.WrongPeriod;
        return PromoCheck.Ok;
    }

    /// <summary>Лучшая скидка для тарифа: акция по дате или введённый промокод (не суммируются).</summary>
    public Discount? Best(string tier, BillingPeriod period, string? code, DateTimeOffset now)
    {
        Discount? best = null;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (var e in ActiveEvents(today).Where(e => e.Tiers.Count == 0 || e.Tiers.Contains(tier)))
            if (best is null || e.Percent > best.Percent)
                best = new Discount(e.Percent, "event:" + e.Id, e.Title, null, null, e.Emoji);

        if (code is { Length: > 0 } && Check(code, tier, period, now, out var p) == PromoCheck.Ok && (best is null || p!.Percent > best.Percent))
            best = new Discount(p!.Percent, "code", Normalize(code), Normalize(code), p.Partner);

        return best is { Percent: > 0 and < 100 } ? best : null;
    }

    public static string Explain(PromoCheck c) => c switch
    {
        PromoCheck.Ok => "Промокод применён",
        PromoCheck.Unknown => "Такого промокода нет",
        PromoCheck.Expired => "Срок действия промокода закончился",
        PromoCheck.NotYet => "Промокод ещё не начал действовать",
        PromoCheck.WrongTier => "Промокод не подходит для этого тарифа",
        PromoCheck.WrongPeriod => "Промокод действует для другого периода оплаты",
        _ => "",
    };
}
