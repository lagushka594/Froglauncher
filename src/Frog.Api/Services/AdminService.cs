using System.Text;
using Frog.Api.Data;
using Frog.Api.Payments;
using Frog.Shared;
using Microsoft.EntityFrameworkCore;

namespace Frog.Api.Services;

/// <summary>
/// Операции FrogNews над пользователями лаунчера: поиск, выдача/отзыв Admin, аудит.
/// Отзыв Admin = отзыв всех refresh-токенов + инкремент TokenVersion → принудительный перелогин.
/// </summary>
public sealed class AdminService(
    FrogDbContext db,
    EntitlementService entitlements,
    JwtService jwt,
    TierCatalog tiers,
    PromoCatalog promos,
    TimeProvider clock,
    ILogger<AdminService> log)
{
    public async Task<IReadOnlyList<PromoStatDto>> PromoStatsAsync(CancellationToken ct)
    {
        var rows = await db.Payments
            .Where(p => p.Status == "succeeded" && p.Note != null && p.Note.StartsWith("promo:"))
            .Select(p => new { p.Note, p.Amount })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Note!["promo:".Length..])
            .Select(g => new PromoStatDto(g.Key, promos.Find(g.Key)?.Partner, g.Count(), g.Sum(r => r.Amount)))
            .OrderByDescending(x => x.Payments)
            .ToList();
    }

    public const int MinReasonLength = 5;

    public async Task<PagedResult<AdminUserDto>> SearchUsersAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            q = q.Where(u => u.LauncherNick.ToLower().Contains(s) || u.Email.ToLower().Contains(s));
        }

        var total = await q.CountAsync(ct);
        var users = await q.OrderBy(u => u.LauncherNick).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var grants = await db.AdminGrants.AsNoTracking()
            .Where(g => ids.Contains(g.UserId) && g.RevokedAt == null)
            .ToDictionaryAsync(g => g.UserId, g => g.Id, ct);

        var items = new List<AdminUserDto>(users.Count);
        foreach (var u in users)
        {
            var ent = await entitlements.ResolveAsync(u.Id, ct);
            var grantId = grants.TryGetValue(u.Id, out var gid) ? gid : (Guid?)null;
            items.Add(new AdminUserDto(u.Id, u.LauncherNick, Pii.MaskEmail(u.Email), ent.Tier, ent.PeriodEnd,
                grantId is not null, grantId, u.CreatedAt));
        }

        return new PagedResult<AdminUserDto>(items, total, page, pageSize);
    }

    public async Task<AdminGrantDto> GrantAsync(GrantRequest req, string grantedBy, CancellationToken ct)
    {
        if (req.TierSlug != TierSlugs.Admin)
            throw new BillingException("Через FrogNews выдаётся только тир admin");
        ValidateReason(req.Reason);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId, ct)
                   ?? throw new BillingException("Пользователь не найден", 404);
        if (await db.AdminGrants.AnyAsync(g => g.UserId == user.Id && g.RevokedAt == null, ct))
            throw new BillingException("Admin уже выдан этому пользователю", 409);

        var grant = new AdminGrant
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GrantedBy = grantedBy,
            GrantedAt = clock.GetUtcNow(),
            Reason = req.Reason.Trim(),
        };
        db.AdminGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        await entitlements.RefreshCacheAsync(user.Id, ct);

        log.LogWarning("AUDIT: {By} выдал Admin пользователю {Nick} (grant {GrantId})", grantedBy, user.LauncherNick, grant.Id);
        return ToDto(grant, user.LauncherNick);
    }

    public async Task<AdminGrantDto> RevokeAsync(RevokeRequest req, string revokedBy, CancellationToken ct)
    {
        ValidateReason(req.Reason);
        var grant = await db.AdminGrants.Include(g => g.User).FirstOrDefaultAsync(g => g.Id == req.GrantId, ct)
                    ?? throw new BillingException("Выдача не найдена", 404);
        if (grant.RevokedAt is not null)
            throw new BillingException("Admin уже снят", 409);

        grant.RevokedAt = clock.GetUtcNow();
        grant.RevokedBy = revokedBy;
        grant.RevokeReason = req.Reason.Trim();
        await db.SaveChangesAsync(ct);

        // Инвалидация сессий: старый access-токен с tier=admin больше не примут.
        await jwt.ForceReloginAsync(grant.UserId, ct);
        await entitlements.RefreshCacheAsync(grant.UserId, ct);

        log.LogWarning("AUDIT: {By} снял Admin с {Nick} (grant {GrantId}), токены отозваны",
            revokedBy, grant.User!.LauncherNick, grant.Id);
        return ToDto(grant, grant.User.LauncherNick);
    }

    // ---------- Платные тарифы вручную (покупка через тикет в Discord) ----------

    private static readonly SubscriptionStatus[] ManualLive =
        { SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Canceled };

    /// <summary>
    /// Выдать платный тариф на месяц/год. Тот же тариф — срок продлевается от текущей даты окончания;
    /// другой тариф — прежние ручные подписки заменяются новой. В историю платежей пишется запись с суммой по прайсу.
    /// </summary>
    public async Task<SubscriptionGrantDto> GrantSubscriptionAsync(GrantSubscriptionRequest req, string grantedBy, CancellationToken ct)
    {
        if (!tiers.IsPurchasable(req.TierSlug))
            throw new BillingException("Этот тариф нельзя выдать как подписку");
        ValidateReason(req.Reason);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId, ct)
                   ?? throw new BillingException("Пользователь не найден", 404);
        var now = clock.GetUtcNow();

        var live = await db.Subscriptions
            .Where(s => s.UserId == user.Id && s.Provider == ManualPaymentProvider.ProviderName && ManualLive.Contains(s.Status))
            .ToListAsync(ct);

        var sub = live.FirstOrDefault(s => s.TierSlug == req.TierSlug);
        var kind = sub is null ? PaymentKind.Initial : PaymentKind.Renewal;
        if (sub is null)
        {
            sub = new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TierSlug = req.TierSlug,
                Provider = ManualPaymentProvider.ProviderName,
                CreatedAt = now,
            };
            sub.ProviderSubId = $"manual_{sub.Id:N}";
            db.Subscriptions.Add(sub);
        }

        foreach (var other in live.Where(s => s != sub))
        {
            other.Status = SubscriptionStatus.Replaced;
            other.CanceledAt = now;
        }

        var from = sub.CurrentPeriodEnd is { } end && end > now ? end : now;
        sub.Period = req.Period;
        sub.CurrentPeriodEnd = ProrationCalculator.AddPeriod(from, req.Period);
        sub.Status = SubscriptionStatus.Active;
        sub.GraceUntil = null;
        sub.CancelAtPeriodEnd = false;
        sub.CanceledAt = null;

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            UserId = user.Id,
            Provider = ManualPaymentProvider.ProviderName,
            ProviderPaymentId = $"manual_{Guid.NewGuid():N}",
            TierSlug = req.TierSlug,
            Period = req.Period,
            Amount = tiers.PriceFor(req.TierSlug, req.Period),
            Currency = "RUB",
            Status = "succeeded",
            Kind = kind,
            CreatedAt = now,
            GrantedBy = grantedBy,
            Note = req.Reason.Trim(),
        });
        await db.SaveChangesAsync(ct);
        var ent = await entitlements.RefreshCacheAsync(user.Id, ct);

        log.LogWarning("AUDIT: {By} выдал {Tier}/{Period} пользователю {Nick} до {End:u}",
            grantedBy, req.TierSlug, req.Period, user.LauncherNick, sub.CurrentPeriodEnd);
        return new SubscriptionGrantDto(user.Id, ent.Tier, ent.PeriodEnd);
    }

    /// <summary>Снять ручную подписку (возврат, ошибка выдачи). Тир сразу падает до tadpole.</summary>
    public async Task<SubscriptionGrantDto> RevokeSubscriptionAsync(RevokeSubscriptionRequest req, string revokedBy, CancellationToken ct)
    {
        ValidateReason(req.Reason);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId, ct)
                   ?? throw new BillingException("Пользователь не найден", 404);
        var now = clock.GetUtcNow();

        var live = await db.Subscriptions
            .Where(s => s.UserId == user.Id && s.Provider == ManualPaymentProvider.ProviderName && ManualLive.Contains(s.Status))
            .ToListAsync(ct);
        if (live.Count == 0) throw new BillingException("У пользователя нет подписки, выданной вручную", 404);

        foreach (var sub in live)
        {
            sub.Status = SubscriptionStatus.Expired;
            sub.CanceledAt = now;
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                SubscriptionId = sub.Id,
                UserId = user.Id,
                Provider = ManualPaymentProvider.ProviderName,
                TierSlug = sub.TierSlug,
                Period = sub.Period,
                Amount = 0,
                Currency = "RUB",
                Status = "revoked",
                Kind = PaymentKind.Renewal,
                CreatedAt = now,
                GrantedBy = revokedBy,
                Note = req.Reason.Trim(),
            });
        }

        await db.SaveChangesAsync(ct);
        var ent = await entitlements.RefreshCacheAsync(user.Id, ct);
        log.LogWarning("AUDIT: {By} снял подписку с {Nick}", revokedBy, user.LauncherNick);
        return new SubscriptionGrantDto(user.Id, ent.Tier, ent.PeriodEnd);
    }

    /// <summary>Аудит: выдачи Admin и ручные выдачи/снятия платных тарифов, новые сверху.</summary>
    public async Task<IReadOnlyList<AdminGrantDto>> GrantsAsync(Guid? userId, CancellationToken ct)
    {
        var q = db.AdminGrants.AsNoTracking().Include(g => g.User).AsQueryable();
        if (userId is { } id) q = q.Where(g => g.UserId == id);
        var admin = (await q.OrderByDescending(g => g.GrantedAt).Take(1000).ToListAsync(ct))
            .Select(g => ToDto(g, g.User?.LauncherNick ?? "?"));

        var pq = db.Payments.AsNoTracking().Where(p => p.Provider == ManualPaymentProvider.ProviderName);
        if (userId is { } uid) pq = pq.Where(p => p.UserId == uid);
        var payments = await pq.OrderByDescending(p => p.CreatedAt).Take(1000).ToListAsync(ct);
        var ids = payments.Select(p => p.UserId).Distinct().ToList();
        var nicks = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.LauncherNick, ct);

        var manual = payments.Select(p =>
        {
            var tier = $"{tiers.Get(p.TierSlug)?.Name ?? p.TierSlug} · {(p.Period == BillingPeriod.Year ? "год" : "месяц")}";
            var nick = nicks.GetValueOrDefault(p.UserId, "?");
            return p.Status == "revoked"
                ? new AdminGrantDto(p.Id, p.UserId, nick, "—", p.CreatedAt, p.CreatedAt, p.GrantedBy, "", p.Note, tier)
                : new AdminGrantDto(p.Id, p.UserId, nick, p.GrantedBy ?? "?", p.CreatedAt, null, null, p.Note ?? "", null, tier);
        });

        return admin.Concat(manual).OrderByDescending(g => g.RevokedAt ?? g.GrantedAt).ToList();
    }

    /// <summary>CSV аудита: одна строка на событие (выдача или отзыв). UTF-8 с BOM — корректно открывается в Excel.</summary>
    public async Task<byte[]> ExportCsvAsync(CancellationToken ct)
    {
        var grants = await GrantsAsync(null, ct);
        var sb = new StringBuilder();
        sb.AppendLine("event,grant_id,user_id,user_nick,tier,actor,at_utc,reason");
        foreach (var g in grants.OrderBy(g => g.GrantedAt))
        {
            // Ручное снятие подписки — отдельная запись без выдачи.
            if (g.GrantedBy != "—") Row(sb, "grant", g, g.GrantedBy, g.GrantedAt, g.Reason);
            if (g.RevokedAt is { } at) Row(sb, "revoke", g, g.RevokedBy ?? "", at, g.RevokeReason ?? "");
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static void Row(StringBuilder sb, string evt, AdminGrantDto g, string actor, DateTimeOffset at, string reason) =>
        sb.AppendLine(string.Join(',', evt, g.Id, g.UserId, Csv(g.UserNick), Csv(g.Tier), Csv(actor), at.UtcDateTime.ToString("u"), Csv(reason)));

    /// <summary>Экранирование CSV + защита от formula injection (=, +, -, @ в начале ячейки).</summary>
    internal static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static void ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinReasonLength)
            throw new BillingException($"Причина обязательна (минимум {MinReasonLength} символов)");
    }

    private static AdminGrantDto ToDto(AdminGrant g, string nick) =>
        new(g.Id, g.UserId, nick, g.GrantedBy, g.GrantedAt, g.RevokedAt, g.RevokedBy, g.Reason, g.RevokeReason, "Admin");
}
