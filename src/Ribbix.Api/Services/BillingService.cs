using Ribbix.Api.Data;
using Ribbix.Api.Options;
using Ribbix.Api.Payments;
using Ribbix.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ribbix.Api.Services;

/// <summary>Ошибка бизнес-правил биллинга → HTTP 400/409 с понятным текстом.</summary>
public sealed class BillingException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Подписки: checkout (в т.ч. апгрейд с прорейтом), применение вебхуков, отмена, история.
/// Вся логика не зависит от провайдера — провайдеры отдают нормализованные события.
/// </summary>
public sealed class BillingService(
    RibbixDbContext db,
    TierCatalog tiers,
    PromoCatalog promos,
    PaymentProviderRegistry providers,
    EntitlementService entitlements,
    IOptions<BillingOptions> options,
    TimeProvider clock,
    ILogger<BillingService> log)
{
    private readonly BillingOptions _o = options.Value;

    private static readonly SubscriptionStatus[] Live =
        { SubscriptionStatus.Active, SubscriptionStatus.Canceled, SubscriptionStatus.PastDue };

    public async Task<CheckoutResponse> CheckoutAsync(Guid userId, CheckoutRequest req, CancellationToken ct)
    {
        if (!tiers.IsPurchasable(req.TierSlug))
            throw new BillingException("Этот тариф нельзя купить");

        if (await db.AdminGrants.AnyAsync(g => g.UserId == userId && g.RevokedAt == null, ct))
            throw new BillingException("У вас уже есть Admin — покупка не нужна", 409);

        var provider = providers.Get(req.Provider ?? _o.DefaultProvider);
        var tier = tiers.Get(req.TierSlug)!;
        var price = tiers.PriceFor(req.TierSlug, req.Period);
        var now = clock.GetUtcNow();

        // Неверный код — явная ошибка, а не тихая оплата без скидки.
        if (req.PromoCode is { Length: > 0 } code &&
            promos.Check(code, req.TierSlug, req.Period, now, out _) is var check and not PromoCheck.Ok)
            throw new BillingException(PromoCatalog.Explain(check));
        var discount = promos.Best(req.TierSlug, req.Period, req.PromoCode, now);
        if (discount is not null) price = discount.Apply(price);

        var current = (await db.Subscriptions
                .Where(s => s.UserId == userId && Live.Contains(s.Status))
                .ToListAsync(ct))
            .Where(s => entitlements.Evaluate(s).valid)
            .OrderByDescending(s => tiers.RankOf(s.TierSlug))
            .FirstOrDefault();

        var kind = PaymentKind.Initial;
        var amount = price;

        if (current is not null)
        {
            var curRank = tiers.RankOf(current.TierSlug);
            var newRank = tiers.RankOf(req.TierSlug);

            if (newRank < curRank || (newRank == curRank && current.Period == req.Period))
                throw new BillingException(newRank < curRank
                    ? "Понижение тарифа: отключите автопродление — после окончания периода оформите новый тариф"
                    : "Этот тариф уже активен", 409);

            kind = PaymentKind.Upgrade;

            // Paddle: план меняем у провайдера, он сам считает и списывает доплату.
            if (provider.SupportsNativePlanChange && current.Provider == provider.Name && current.ProviderSubId is not null)
            {
                await provider.ChangePlanAsync(current, req.TierSlug, req.Period, ct);
                log.LogInformation("Paddle: смена плана {SubId} → {Tier}", current.Id, req.TierSlug);
                return new CheckoutResponse(ReturnUrl(current.Id), current.Id, 0, "RUB", IsUpgrade: true);
            }

            var quote = ProrationCalculator.Quote(
                tiers.PriceFor(current.TierSlug, current.Period), current.Period, current.CurrentPeriodEnd!.Value,
                price, req.Period, now);
            amount = quote.Amount;
        }

        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TierSlug = req.TierSlug,
            Period = req.Period,
            Status = SubscriptionStatus.Pending,
            Provider = provider.Name,
            ReplacesSubscriptionId = current?.Id,
            // Наследуем сохранённую карту, чтобы продления апгрейда шли без повторного ввода.
            ProviderPaymentMethodId = current?.Provider == provider.Name ? current.ProviderPaymentMethodId : null,
            CreatedAt = now,
        };
        if (!provider.SupportsNativePlanChange) sub.ProviderSubId = $"yk_{sub.Id:N}";

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = sub.Id,
            UserId = userId,
            Provider = provider.Name,
            TierSlug = req.TierSlug,
            Period = req.Period,
            Amount = amount,
            Currency = "RUB",
            Status = "pending",
            Kind = kind,
            CreatedAt = now,
            Note = discount switch
            {
                { Code: { } c } => "promo:" + c,
                { } d => d.Source,
                null => null,
            },
        };

        db.Subscriptions.Add(sub);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        var session = await provider.CreateCheckoutAsync(new CheckoutContext(
            sub.Id, payment.Id, userId, tier.Slug, tier.Name, req.Period, amount, "RUB", ReturnUrl(sub.Id), kind), ct);

        payment.ProviderPaymentId = session.ProviderPaymentId;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Checkout {Tier}/{Period} через {Provider}, сумма {Amount}, апгрейд={Upgrade}",
            req.TierSlug, req.Period, provider.Name, amount, kind == PaymentKind.Upgrade);
        return new CheckoutResponse(session.PaymentUrl, sub.Id, amount, "RUB", kind == PaymentKind.Upgrade);
    }

    /// <summary>
    /// Обработка вебхука. Идемпотентно: повторная доставка того же события ничего не меняет.
    /// Возвращает false, если подпись/источник не прошли проверку.
    /// </summary>
    public async Task<bool> HandleWebhookAsync(string providerName, WebhookRequest req, CancellationToken ct)
    {
        if (!providers.TryGet(providerName, out var provider)) return false;

        var result = await provider.ParseWebhookAsync(req, ct);
        if (!result.Authentic)
        {
            log.LogWarning("Отклонён вебхук {Provider}: {Error}", providerName, result.Error);
            return false;
        }

        if (result.EventId is not null)
        {
            var seen = await db.WebhookEvents.AnyAsync(e => e.Provider == provider.Name && e.EventId == result.EventId, ct);
            if (seen) return true;
            db.WebhookEvents.Add(new WebhookEvent
            {
                Id = Guid.NewGuid(), Provider = provider.Name, EventId = result.EventId,
                EventType = result.EventType, ReceivedAt = clock.GetUtcNow(),
            });
        }

        var userId = result.Event is null ? null : await ApplyAsync(provider.Name, result.Event, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (result.EventId is not null)
        {
            // Параллельная доставка того же события — уникальный индекс webhook_events сработал.
            log.LogInformation("Дубликат вебхука {EventId}", result.EventId);
            return true;
        }

        if (userId is { } uid) await entitlements.RefreshCacheAsync(uid, ct);
        return true;
    }

    /// <summary>Применяет нормализованное событие. Возвращает пользователя, чьи права изменились.</summary>
    internal async Task<Guid?> ApplyAsync(string provider, BillingEvent evt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        switch (evt)
        {
            case PaymentSucceeded e:
            {
                var payment = await FindPaymentAsync(provider, e.PaymentId, e.ProviderPaymentId, ct);
                var sub = await FindSubscriptionAsync(provider, e.SubscriptionId ?? payment?.SubscriptionId, e.ProviderSubId, ct);
                if (sub is null)
                {
                    log.LogWarning("Оплата {PaymentId} без подписки", e.ProviderPaymentId);
                    return null;
                }

                if (payment is null)
                {
                    // Продление, инициированное провайдером (Paddle) — платежа у нас ещё нет.
                    payment = NewPayment(sub, PaymentKind.Renewal, tiers.PriceFor(sub.TierSlug, sub.Period));
                    db.Payments.Add(payment);
                }
                if (payment.Status == "succeeded") return null; // уже учтён

                payment.Status = "succeeded";
                payment.ProviderPaymentId ??= e.ProviderPaymentId;
                sub.ProviderSubId ??= e.ProviderSubId;
                sub.ProviderPaymentMethodId = e.PaymentMethodId ?? sub.ProviderPaymentMethodId;

                if (sub.Status == SubscriptionStatus.Pending)
                    await ActivateAsync(sub, payment, e.PeriodEnd, now, ct);
                else
                {
                    // Продление: от конца текущего периода, а если уже просрочено — от текущего момента.
                    var from = sub.CurrentPeriodEnd is { } end && end > now ? end : now;
                    sub.CurrentPeriodEnd = e.PeriodEnd ?? ProrationCalculator.AddPeriod(from, sub.Period);
                    if (sub.Status == SubscriptionStatus.PastDue) sub.Status = SubscriptionStatus.Active;
                    sub.GraceUntil = null;
                    sub.FailedRenewalAttempts = 0;
                }

                log.LogInformation("Оплата прошла: подписка {SubId} {Tier} до {End:u}", sub.Id, sub.TierSlug, sub.CurrentPeriodEnd);
                return sub.UserId;
            }

            case PaymentFailed e:
            {
                var payment = await FindPaymentAsync(provider, e.PaymentId, e.ProviderPaymentId, ct);
                var sub = await FindSubscriptionAsync(provider, e.SubscriptionId ?? payment?.SubscriptionId, e.ProviderSubId, ct);
                if (payment is not null) payment.Status = "failed";
                if (sub is null) return null;

                // Неудачная первая оплата — подписка просто не активируется.
                if (sub.Status == SubscriptionStatus.Pending) return null;

                MarkPastDue(sub, now);
                log.LogWarning("Не удалось продлить подписку {SubId}: {Reason}. Grace до {Grace:u}", sub.Id, e.Reason, sub.GraceUntil);
                return sub.UserId;
            }

            case SubscriptionCanceled e:
            {
                var sub = await FindSubscriptionAsync(provider, null, e.ProviderSubId, ct);
                if (sub is null) return null;
                sub.Status = SubscriptionStatus.Expired;
                sub.CanceledAt ??= e.EffectiveAt ?? now;
                return sub.UserId;
            }

            case SubscriptionUpdated e:
            {
                var sub = await FindSubscriptionAsync(provider, e.SubscriptionId, e.ProviderSubId, ct);
                if (sub is null) return null;
                sub.ProviderSubId ??= e.ProviderSubId;
                if (e.TierSlug is not null && tiers.IsPurchasable(e.TierSlug)) sub.TierSlug = e.TierSlug;
                if (e.Period is { } p) sub.Period = p;
                if (e.PeriodEnd is { } end) sub.CurrentPeriodEnd = end;
                sub.CancelAtPeriodEnd = e.CancelAtPeriodEnd;

                switch (e.Status)
                {
                    case "past_due": MarkPastDue(sub, now); break;
                    case "active" or "trialing":
                        sub.Status = e.CancelAtPeriodEnd ? SubscriptionStatus.Canceled : SubscriptionStatus.Active;
                        sub.GraceUntil = null;
                        break;
                    case "canceled": sub.Status = SubscriptionStatus.Expired; break;
                }
                return sub.UserId;
            }
        }

        return null;
    }

    private async Task ActivateAsync(Subscription sub, Payment payment, DateTimeOffset? providerEnd, DateTimeOffset now, CancellationToken ct)
    {
        var end = providerEnd ?? ProrationCalculator.AddPeriod(now, sub.Period);

        if (sub.ReplacesSubscriptionId is { } oldId &&
            await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == oldId, ct) is { } old)
        {
            // Апгрейд в том же периоде сохраняет дату окончания старой подписки (за неё и доплатили).
            if (payment.Kind == PaymentKind.Upgrade && old.Period == sub.Period && old.CurrentPeriodEnd > now && providerEnd is null)
                end = old.CurrentPeriodEnd.Value;
            old.Status = SubscriptionStatus.Replaced;
            old.CanceledAt = now;
        }

        sub.Status = SubscriptionStatus.Active;
        sub.CurrentPeriodEnd = end;
        sub.GraceUntil = null;
    }

    private void MarkPastDue(Subscription sub, DateTimeOffset now)
    {
        sub.Status = SubscriptionStatus.PastDue;
        sub.FailedRenewalAttempts++;
        sub.GraceUntil ??= (sub.CurrentPeriodEnd is { } end && end > now ? end : now).AddDays(_o.GraceDays);
    }

    public async Task<CancelResponse> CancelAsync(Guid userId, CancellationToken ct)
    {
        var sub = await db.Subscriptions
            .Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue))
            .OrderByDescending(s => s.CurrentPeriodEnd)
            .FirstOrDefaultAsync(ct)
            ?? throw new BillingException("Нет активной подписки с автопродлением", 404);

        await providers.Get(sub.Provider).CancelAsync(sub, ct);

        sub.CancelAtPeriodEnd = true;
        sub.CanceledAt = clock.GetUtcNow();
        // PastDue при отмене сразу заканчивается: продлевать больше нечего.
        sub.Status = sub.Status == SubscriptionStatus.PastDue ? SubscriptionStatus.Expired : SubscriptionStatus.Canceled;
        await db.SaveChangesAsync(ct);
        await entitlements.RefreshCacheAsync(userId, ct);

        log.LogInformation("Автопродление отключено: подписка {SubId}", sub.Id);
        return new CancelResponse(sub.Status == SubscriptionStatus.Canceled ? sub.CurrentPeriodEnd : null);
    }

    public async Task<IReadOnlyList<PaymentHistoryItem>> HistoryAsync(Guid userId, CancellationToken ct) =>
        await db.Payments
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(100)
            .Select(p => new PaymentHistoryItem(p.Id, p.TierSlug, p.Period, p.Amount, p.Currency, p.Status,
                p.Kind.ToString().ToLower(), p.Provider, p.CreatedAt))
            .ToListAsync(ct);

    // ---------- Жизненный цикл (вызывается фоновым воркером) ----------

    /// <summary>
    /// Один проход обслуживания подписок:
    /// 1) списать продления YooKassa заранее; 2) истёкшие без продления → past_due + grace 7 дней;
    /// 3) отменённые после конца периода → expired; 4) grace кончился → expired (tadpole);
    /// 5) брошенные checkout старше суток → expired.
    /// </summary>
    public async Task RunLifecycleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var touched = new HashSet<Guid>();

        // 1. Рекуррентные списания (только провайдеры без нативных подписок).
        var lead = now.AddHours(_o.RenewalLeadHours);
        var due = await db.Subscriptions
            .Where(s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
                        && !s.CancelAtPeriodEnd && s.CurrentPeriodEnd <= lead
                        && s.ProviderPaymentMethodId != null && s.FailedRenewalAttempts < _o.MaxRenewalAttempts)
            .ToListAsync(ct);

        foreach (var sub in due)
        {
            if (!providers.TryGet(sub.Provider, out var provider) || !provider.SupportsServerSideRenewal) continue;

            // Не чаще раза в сутки и не дублировать висящий платёж.
            var recent = await db.Payments.AnyAsync(p => p.SubscriptionId == sub.Id && p.Kind == PaymentKind.Renewal
                && (p.Status == "pending" || p.CreatedAt > now.AddHours(-23)), ct);
            if (recent) continue;

            var payment = NewPayment(sub, PaymentKind.Renewal, tiers.PriceFor(sub.TierSlug, sub.Period));
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);

            try
            {
                var r = await provider.ChargeRenewalAsync(new RenewalContext(sub.Id, payment.Id, sub.ProviderPaymentMethodId!,
                    payment.Amount, payment.Currency, sub.TierSlug, sub.Period, $"Ribbix: продление {sub.TierSlug}"), ct);
                payment.ProviderPaymentId = r.ProviderPaymentId;

                BillingEvent? immediate = r.Outcome switch
                {
                    PaymentOutcome.Succeeded => new PaymentSucceeded(sub.Id, payment.Id, r.ProviderPaymentId, null, null, null),
                    PaymentOutcome.Failed => new PaymentFailed(sub.Id, payment.Id, r.ProviderPaymentId, null, "declined"),
                    _ => null,
                };
                if (immediate is not null && await ApplyAsync(provider.Name, immediate, ct) is { } uid) touched.Add(uid);
            }
            catch (Exception ex) when (ex is PaymentProviderException or HttpRequestException)
            {
                payment.Status = "failed";
                if (sub.CurrentPeriodEnd <= now) MarkPastDue(sub, now);
                touched.Add(sub.UserId);
                log.LogWarning(ex, "Ошибка рекуррентного списания по подписке {SubId}", sub.Id);
            }

            await db.SaveChangesAsync(ct);
        }

        // 2. Период кончился, продления нет → grace.
        foreach (var sub in await db.Subscriptions
                     .Where(s => s.Status == SubscriptionStatus.Active && s.CurrentPeriodEnd < now).ToListAsync(ct))
        {
            sub.Status = SubscriptionStatus.PastDue;
            sub.GraceUntil ??= sub.CurrentPeriodEnd!.Value.AddDays(_o.GraceDays);
            touched.Add(sub.UserId);
        }

        // 3. Отменённые пользователем — после конца периода сразу tadpole.
        foreach (var sub in await db.Subscriptions
                     .Where(s => s.Status == SubscriptionStatus.Canceled && s.CurrentPeriodEnd < now).ToListAsync(ct))
        {
            sub.Status = SubscriptionStatus.Expired;
            touched.Add(sub.UserId);
        }

        // 4. Grace кончился → даунгрейд до tadpole. Игра при этом продолжает запускаться.
        foreach (var sub in await db.Subscriptions
                     .Where(s => s.Status == SubscriptionStatus.PastDue && s.GraceUntil < now).ToListAsync(ct))
        {
            sub.Status = SubscriptionStatus.Expired;
            touched.Add(sub.UserId);
            log.LogInformation("Grace истёк: подписка {SubId} → tadpole", sub.Id);
        }

        // 5. Брошенные checkout.
        foreach (var sub in await db.Subscriptions
                     .Where(s => s.Status == SubscriptionStatus.Pending && s.CreatedAt < now.AddDays(-1)).ToListAsync(ct))
            sub.Status = SubscriptionStatus.Expired;

        await db.SaveChangesAsync(ct);
        foreach (var uid in touched) await entitlements.RefreshCacheAsync(uid, ct);
    }

    private Payment NewPayment(Subscription sub, PaymentKind kind, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        Provider = sub.Provider,
        TierSlug = sub.TierSlug,
        Period = sub.Period,
        Amount = amount,
        Currency = "RUB",
        Status = "pending",
        Kind = kind,
        CreatedAt = clock.GetUtcNow(),
    };

    private async Task<Payment?> FindPaymentAsync(string provider, Guid? paymentId, string? providerPaymentId, CancellationToken ct)
    {
        if (paymentId is { } id && await db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct) is { } byId) return byId;
        return providerPaymentId is null
            ? null
            : await db.Payments.FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderPaymentId == providerPaymentId, ct);
    }

    private async Task<Subscription?> FindSubscriptionAsync(string provider, Guid? subscriptionId, string? providerSubId, CancellationToken ct)
    {
        if (subscriptionId is { } id && await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct) is { } byId) return byId;
        return providerSubId is null
            ? null
            : await db.Subscriptions.FirstOrDefaultAsync(s => s.Provider == provider && s.ProviderSubId == providerSubId, ct);
    }

    private string ReturnUrl(Guid subId) => $"{_o.PublicBaseUrl.TrimEnd('/')}/billing/return?sub_id={subId}";
}

/// <summary>Фоновый воркер: обслуживание подписок раз в 15 минут.</summary>
public sealed class SubscriptionLifecycleWorker(IServiceScopeFactory scopes, ILogger<SubscriptionLifecycleWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingService>().RunLifecycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Ошибка обслуживания подписок");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
