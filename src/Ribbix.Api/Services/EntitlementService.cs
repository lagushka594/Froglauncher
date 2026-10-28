using System.Text.Json;
using Ribbix.Api.Data;
using Ribbix.Api.Options;
using Ribbix.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ribbix.Api.Services;

/// <summary>
/// Вычисляет текущий тир и фичи пользователя.
/// Приоритет: активная выдача Admin → лучшая действующая подписка → tadpole.
/// Grace: подписка с неудачным продлением ещё 7 дней даёт фичи и флаг in_grace.
/// </summary>
public sealed class EntitlementService(
    RibbixDbContext db,
    TierCatalog tiers,
    IOptions<BillingOptions> billing,
    TimeProvider clock)
{
    private static readonly SubscriptionStatus[] LiveStatuses =
        { SubscriptionStatus.Active, SubscriptionStatus.Canceled, SubscriptionStatus.PastDue };

    public async Task<EntitlementsDto> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var hasAdmin = await db.AdminGrants.AnyAsync(g => g.UserId == userId && g.RevokedAt == null, ct);
        if (hasAdmin && tiers.Get(TierSlugs.Admin) is { } admin)
            return new EntitlementsDto(admin.Slug, admin.Entitlements, null, false, null);

        var subs = await db.Subscriptions
            .Where(s => s.UserId == userId && LiveStatuses.Contains(s.Status))
            .ToListAsync(ct);

        var best = subs
            .Select(s => (sub: s, state: Evaluate(s)))
            .Where(x => x.state.valid)
            .OrderByDescending(x => tiers.RankOf(x.sub.TierSlug))
            .FirstOrDefault();

        if (best.sub is null || tiers.Get(best.sub.TierSlug) is not { } tier)
            return new EntitlementsDto(TierSlugs.Tadpole, tiers.Tadpole.Entitlements, null, false, null);

        return new EntitlementsDto(tier.Slug, tier.Entitlements, best.sub.CurrentPeriodEnd,
            best.state.inGrace, best.state.inGrace ? best.state.graceUntil : null, best.sub.CancelAtPeriodEnd);
    }

    /// <summary>Действует ли подписка прямо сейчас (с учётом grace).</summary>
    internal (bool valid, bool inGrace, DateTimeOffset? graceUntil) Evaluate(Subscription s)
    {
        var now = clock.GetUtcNow();
        var end = s.CurrentPeriodEnd;
        if (end is null) return (false, false, null);

        switch (s.Status)
        {
            case SubscriptionStatus.Active when end > now:
            case SubscriptionStatus.Canceled when end > now:
                return (true, false, null);

            // Период кончился, а продление ещё не пришло (воркер не успел) — сразу считаем grace.
            case SubscriptionStatus.Active:
            {
                var until = end.Value.AddDays(billing.Value.GraceDays);
                return (until > now, until > now, until);
            }

            case SubscriptionStatus.PastDue:
            {
                var until = s.GraceUntil ?? end.Value.AddDays(billing.Value.GraceDays);
                return (until > now, until > now, until);
            }

            // Отменённая пользователем подписка после окончания периода — сразу tadpole, без grace.
            default:
                return (false, false, null);
        }
    }

    /// <summary>Пересчитать и сохранить entitlements_cache. Вызывается после любых изменений подписки.</summary>
    public async Task<EntitlementsDto> RefreshCacheAsync(Guid userId, CancellationToken ct = default)
    {
        var dto = await ResolveAsync(userId, ct);
        var json = JsonSerializer.Serialize(dto, RibbixJson.Options);
        var row = await db.EntitlementsCache.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (row is null)
            db.EntitlementsCache.Add(new EntitlementsCache { UserId = userId, Json = json, UpdatedAt = clock.GetUtcNow() });
        else
        {
            row.Json = json;
            row.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return dto;
    }
}
