using Ribbix.Shared;

namespace Ribbix.Api.Services;

public sealed record ProrationQuote(decimal Amount, DateTimeOffset NewPeriodEnd, bool KeepsPeriodEnd);

/// <summary>
/// Прорейт апгрейда (ribbix_lite → ribbix_plus и т.п.).
/// Тот же период: доплата = (новая цена − старая) × доля оставшегося времени, дата окончания сохраняется.
/// Смена периода (месяц → год): новый период с сегодняшнего дня, неиспользованный остаток старого идёт в зачёт.
/// </summary>
public static class ProrationCalculator
{
    /// <summary>Минимальная сумма платежа у YooKassa — 1 ₽.</summary>
    public const decimal MinCharge = 1m;

    public static DateTimeOffset AddPeriod(DateTimeOffset from, BillingPeriod p) =>
        p == BillingPeriod.Year ? from.AddYears(1) : from.AddMonths(1);

    public static ProrationQuote Quote(
        decimal oldPrice, BillingPeriod oldPeriod, DateTimeOffset oldPeriodEnd,
        decimal newPrice, BillingPeriod newPeriod, DateTimeOffset now)
    {
        var start = oldPeriod == BillingPeriod.Year ? oldPeriodEnd.AddYears(-1) : oldPeriodEnd.AddMonths(-1);
        var total = (oldPeriodEnd - start).TotalSeconds;
        var left = Math.Clamp((oldPeriodEnd - now).TotalSeconds, 0, total);
        var fraction = total <= 0 ? 0m : (decimal)(left / total);

        if (oldPeriod == newPeriod && left > 0)
        {
            var diff = Math.Round((newPrice - oldPrice) * fraction, 2, MidpointRounding.AwayFromZero);
            return new ProrationQuote(Math.Max(diff, MinCharge), oldPeriodEnd, KeepsPeriodEnd: true);
        }

        var credit = Math.Round(oldPrice * fraction, 2, MidpointRounding.AwayFromZero);
        return new ProrationQuote(Math.Max(newPrice - credit, MinCharge), AddPeriod(now, newPeriod), KeepsPeriodEnd: false);
    }
}
