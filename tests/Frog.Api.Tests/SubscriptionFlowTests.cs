using System.Net;
using System.Net.Http.Json;
using Frog.Api.Data;
using Frog.Shared;
using Microsoft.EntityFrameworkCore;

namespace Frog.Api.Tests;

/// <summary>
/// Сценарии чек-листа тестирования подписок — сквозь HTTP API.
/// Каждый тест — свой экземпляр backend (своя БД и своё время).
/// </summary>
public class SubscriptionFlowTests
{
    private static async Task<(ApiFactory api, TokenPair user)> StartAsync(string nick = "froggy")
    {
        var api = new ApiFactory();
        return (api, await api.RegisterAsync(nick));
    }

    private static async Task<EntitlementsDto> EntitlementsAsync(ApiFactory api, string token) =>
        await ApiFactory.ReadAsync<EntitlementsDto>(await api.ClientWith(token).GetAsync("/entitlements"));

    private static async Task<CheckoutResponse> CheckoutAsync(ApiFactory api, string token, string tier, BillingPeriod period) =>
        await ApiFactory.ReadAsync<CheckoutResponse>(await api.ClientWith(token)
            .PostAsJsonAsync("/billing/checkout", new CheckoutRequest(tier, period), FrogJson.Options));

    /// <summary>Оплатить последний pending-платёж пользователя через вебхук.</summary>
    private static async Task PayLastAsync(ApiFactory api, string? eventId = null, string type = "succeeded")
    {
        var pid = await api.WithDbAsync(db => db.Payments.OrderByDescending(p => p.CreatedAt).Select(p => p.ProviderPaymentId).FirstAsync());
        var resp = await api.SendWebhookAsync(new FakePaymentProvider.FakeEvent(eventId ?? Guid.NewGuid().ToString(), type, pid!));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Tiers_endpoint_hides_admin_and_game_works_on_tadpole()
    {
        var (api, user) = await StartAsync();
        var tiers = await ApiFactory.ReadAsync<TiersResponse>(await api.CreateClient().GetAsync("/tiers"));
        Assert.DoesNotContain(tiers.Tiers, t => t.Slug == TierSlugs.Admin);
        Assert.Equal(5, tiers.Tiers.Count);

        var ent = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.Tadpole, ent.Tier);
        Assert.Contains(EntitlementKeys.Base, ent.Entitlements);
    }

    [Theory]
    [InlineData(BillingPeriod.Month, 50, 31)]
    [InlineData(BillingPeriod.Year, 500, 366)]
    public async Task Purchase_month_and_year(BillingPeriod period, decimal price, int maxDays)
    {
        var (api, user) = await StartAsync();
        var checkout = await CheckoutAsync(api, user.AccessToken, TierSlugs.Froglet, period);
        Assert.Equal(price, checkout.Amount);
        Assert.StartsWith("https://pay.test/", checkout.PaymentUrl);

        // До оплаты — всё ещё tadpole.
        Assert.Equal(TierSlugs.Tadpole, (await EntitlementsAsync(api, user.AccessToken)).Tier);

        await PayLastAsync(api);
        var ent = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.Froglet, ent.Tier);
        Assert.Contains(EntitlementKeys.NoAds, ent.Entitlements);
        var days = (ent.PeriodEnd!.Value - api.Clock.GetUtcNow()).TotalDays;
        Assert.InRange(days, maxDays - 3, maxDays);
        Assert.False(ent.InGrace);
    }

    [Fact]
    public async Task Upgrade_froglet_to_frog_plus_is_prorated_and_keeps_period_end()
    {
        var (api, user) = await StartAsync();
        await CheckoutAsync(api, user.AccessToken, TierSlugs.Froglet, BillingPeriod.Month);
        await PayLastAsync(api);
        var before = await EntitlementsAsync(api, user.AccessToken);

        api.Clock.Advance(TimeSpan.FromDays(15)); // половина периода
        var upgrade = await CheckoutAsync(api, user.AccessToken, TierSlugs.FrogPlus, BillingPeriod.Month);
        Assert.True(upgrade.IsUpgrade);
        Assert.InRange(upgrade.Amount, 40, 60); // ≈ (149 − 50) × ½

        await PayLastAsync(api);
        var after = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.FrogPlus, after.Tier);
        Assert.Equal(before.PeriodEnd, after.PeriodEnd);
        Assert.Contains(EntitlementKeys.InstancesUnlimited, after.Entitlements);

        var replaced = await api.WithDbAsync(db => db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Replaced));
        Assert.Equal(1, replaced);
    }

    [Fact]
    public async Task Downgrade_to_tadpole_after_canceled_period_ends()
    {
        var (api, user) = await StartAsync();
        await CheckoutAsync(api, user.AccessToken, TierSlugs.FrogPro, BillingPeriod.Month);
        await PayLastAsync(api);

        var cancel = await ApiFactory.ReadAsync<CancelResponse>(await api.ClientWith(user.AccessToken).PostAsync("/billing/cancel", null));
        Assert.NotNull(cancel.ActiveUntil);

        // До конца периода фичи сохраняются.
        var mid = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.FrogPro, mid.Tier);
        Assert.True(mid.CancelAtPeriodEnd);

        api.Clock.Advance(TimeSpan.FromDays(32));
        await api.RunLifecycleAsync();
        var ent = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.Tadpole, ent.Tier);
        Assert.False(ent.InGrace); // при добровольной отмене grace не нужен
    }

    [Fact]
    public async Task Failed_renewal_gives_7_day_grace_then_tadpole()
    {
        var (api, user) = await StartAsync();
        await CheckoutAsync(api, user.AccessToken, TierSlugs.FrogPlus, BillingPeriod.Month);
        await PayLastAsync(api);

        // За сутки до конца периода воркер пытается списать продление…
        api.Clock.Advance(TimeSpan.FromDays(30.5));
        await api.RunLifecycleAsync();
        var renewal = await api.WithDbAsync(db => db.Payments.CountAsync(p => p.Kind == PaymentKind.Renewal));
        Assert.Equal(1, renewal);

        // …и карта отклоняет платёж.
        await PayLastAsync(api, type: "failed");
        var grace = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.FrogPlus, grace.Tier);
        Assert.True(grace.InGrace);
        Assert.NotNull(grace.GraceUntil);
        Assert.InRange((grace.GraceUntil!.Value - grace.PeriodEnd!.Value).TotalDays, 6.9, 7.1);

        api.Clock.Advance(TimeSpan.FromDays(9));
        await api.RunLifecycleAsync();
        var after = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.Tadpole, after.Tier);
        Assert.False(after.InGrace);
    }

    [Fact]
    public async Task Webhook_is_idempotent_and_requires_authenticity()
    {
        var (api, user) = await StartAsync();
        await CheckoutAsync(api, user.AccessToken, TierSlugs.Froglet, BillingPeriod.Month);
        var pid = await api.WithDbAsync(db => db.Payments.Select(p => p.ProviderPaymentId).FirstAsync());

        var forged = await api.SendWebhookAsync(new FakePaymentProvider.FakeEvent("e1", "succeeded", pid!), secret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(TierSlugs.Tadpole, (await EntitlementsAsync(api, user.AccessToken)).Tier);

        await PayLastAsync(api, eventId: "e1");
        var first = await EntitlementsAsync(api, user.AccessToken);
        await PayLastAsync(api, eventId: "e1"); // повторная доставка
        await PayLastAsync(api, eventId: "e2"); // другой event id, тот же платёж
        var second = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(first.PeriodEnd, second.PeriodEnd); // срок не продлился дважды
    }

    [Fact]
    public async Task Admin_grant_via_frognews_then_revoke_invalidates_tokens()
    {
        var (api, user) = await StartAsync("adminfrog");
        await api.SeedStaffAsync("boss", "superadmin", "superadmin-password");
        await api.SeedStaffAsync("editor", "news_admin", "news-admin-password");

        // Админ новостей — не superadmin: доступа к подпискам нет.
        var editor = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/frognews/login", new StaffLoginRequest("editor", "news-admin-password"), FrogJson.Options));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientWith(editor.AccessToken).GetAsync("/admin/users")).StatusCode);

        // Токен лаунчера не подходит для /admin (другой audience и ключ).
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientWith(user.AccessToken).GetAsync("/admin/users")).StatusCode);

        var boss = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/frognews/login", new StaffLoginRequest("boss", "superadmin-password"), FrogJson.Options));
        var admin = api.ClientWith(boss.AccessToken);

        var users = await ApiFactory.ReadAsync<PagedResult<AdminUserDto>>(await admin.GetAsync("/admin/users?search=adminfr"));
        var target = Assert.Single(users.Items);
        Assert.Equal("a***g@t***.ru", target.EmailMasked);

        // Причина обязательна.
        var noReason = await admin.PostAsJsonAsync("/admin/grant", new GrantRequest(target.Id, TierSlugs.Admin, " "), FrogJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var grant = await ApiFactory.ReadAsync<AdminGrantDto>(await admin.PostAsJsonAsync("/admin/grant",
            new GrantRequest(target.Id, TierSlugs.Admin, "Модератор сообщества"), FrogJson.Options));
        Assert.Equal("boss", grant.GrantedBy);

        var ent = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.Admin, ent.Tier);
        Assert.Contains(EntitlementKeys.AdminBadge, ent.Entitlements);

        // Admin не может купить подписку.
        var buy = await api.ClientWith(user.AccessToken).PostAsJsonAsync("/billing/checkout",
            new CheckoutRequest(TierSlugs.Froglet, BillingPeriod.Month), FrogJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, buy.StatusCode);

        // Отзыв → старый access и refresh больше не работают.
        await ApiFactory.ReadAsync<AdminGrantDto>(await admin.PostAsJsonAsync("/admin/revoke",
            new RevokeRequest(grant.Id, "Покинул команду"), FrogJson.Options));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientWith(user.AccessToken).GetAsync("/entitlements")).StatusCode);
        var refresh = await api.CreateClient().PostAsJsonAsync("/auth/refresh", new RefreshRequest(user.RefreshToken), FrogJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // Повторный вход — уже tadpole.
        var relogin = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/login", new LoginRequest("adminfrog", "password123"), FrogJson.Options));
        Assert.Equal(TierSlugs.Tadpole, (await EntitlementsAsync(api, relogin.AccessToken)).Tier);

        // Аудит: выдача и отзыв.
        var csv = await (await admin.GetAsync("/admin/grants/export")).Content.ReadAsStringAsync();
        Assert.Contains("grant,", csv);
        Assert.Contains("revoke,", csv);
        Assert.Contains("Покинул команду", csv);
    }

    /// <summary>Покупка через тикет в Discord: тариф выдаёт superadmin в FrogNews.</summary>
    [Fact]
    public async Task Manual_grant_from_discord_ticket_extends_switches_and_revokes()
    {
        var (api, user) = await StartAsync("ticketfrog");
        await api.SeedStaffAsync("boss", "superadmin", "superadmin-password");
        var boss = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/frognews/login", new StaffLoginRequest("boss", "superadmin-password"), FrogJson.Options));
        var admin = api.ClientWith(boss.AccessToken);
        var uid = (await ApiFactory.ReadAsync<PagedResult<AdminUserDto>>(await admin.GetAsync("/admin/users?search=ticketfrog"))).Items.Single().Id;

        Task<SubscriptionGrantDto> Grant(string tier, BillingPeriod p, string reason = "Тикет #123") =>
            admin.PostAsJsonAsync("/admin/subscriptions/grant", new GrantSubscriptionRequest(uid, tier, p, reason), FrogJson.Options)
                .ContinueWith(t => ApiFactory.ReadAsync<SubscriptionGrantDto>(t.Result)).Unwrap();

        var first = await Grant(TierSlugs.Froglet, BillingPeriod.Month);
        Assert.Equal(TierSlugs.Froglet, first.Tier);
        Assert.Equal(TierSlugs.Froglet, (await EntitlementsAsync(api, user.AccessToken)).Tier);

        // Повторная выдача того же тарифа продлевает срок от текущей даты окончания.
        var second = await Grant(TierSlugs.Froglet, BillingPeriod.Month, "Тикет #124 продление");
        Assert.InRange((second.PeriodEnd!.Value - first.PeriodEnd!.Value).TotalDays, 27, 32);

        // Другой тариф заменяет прежний.
        var plus = await Grant(TierSlugs.FrogPlus, BillingPeriod.Year, "Тикет #200 апгрейд");
        Assert.Equal(TierSlugs.FrogPlus, plus.Tier);
        Assert.Equal(1, await api.WithDbAsync(db => db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Replaced)));

        // Admin-тир так выдать нельзя, причина обязательна.
        var badTier = await admin.PostAsJsonAsync("/admin/subscriptions/grant", new GrantSubscriptionRequest(uid, TierSlugs.Admin, BillingPeriod.Month, "Тикет #1"), FrogJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, badTier.StatusCode);
        var noReason = await admin.PostAsJsonAsync("/admin/subscriptions/grant", new GrantSubscriptionRequest(uid, TierSlugs.Froglet, BillingPeriod.Month, ""), FrogJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // Платёж виден пользователю в истории.
        var history = await ApiFactory.ReadAsync<List<PaymentHistoryItem>>(await api.ClientWith(user.AccessToken).GetAsync("/billing/history"));
        Assert.Contains(history, h => h.TierSlug == TierSlugs.FrogPlus && h.Amount == 1490 && h.Provider == "manual");

        // Снятие → сразу tadpole; аудит содержит тарифы.
        var revoked = await ApiFactory.ReadAsync<SubscriptionGrantDto>(await admin.PostAsJsonAsync("/admin/subscriptions/revoke",
            new RevokeSubscriptionRequest(uid, "Возврат по тикету #200"), FrogJson.Options));
        Assert.Equal(TierSlugs.Tadpole, revoked.Tier);
        var csv = await (await admin.GetAsync("/admin/grants/export")).Content.ReadAsStringAsync();
        Assert.Contains("Frog+ · год", csv);
        Assert.Contains("Возврат по тикету #200", csv);
    }

    [Fact]
    public async Task Manual_subscription_enters_grace_after_end_then_tadpole()
    {
        var (api, user) = await StartAsync("gracefrog");
        await api.SeedStaffAsync("boss", "superadmin", "superadmin-password");
        var boss = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/frognews/login", new StaffLoginRequest("boss", "superadmin-password"), FrogJson.Options));
        var admin = api.ClientWith(boss.AccessToken);
        var uid = (await ApiFactory.ReadAsync<PagedResult<AdminUserDto>>(await admin.GetAsync("/admin/users?search=gracefrog"))).Items.Single().Id;
        await admin.PostAsJsonAsync("/admin/subscriptions/grant", new GrantSubscriptionRequest(uid, TierSlugs.FrogPro, BillingPeriod.Month, "Тикет #7"), FrogJson.Options);

        api.Clock.Advance(TimeSpan.FromDays(32));
        await api.RunLifecycleAsync();
        var grace = await EntitlementsAsync(api, user.AccessToken);
        Assert.Equal(TierSlugs.FrogPro, grace.Tier);
        Assert.True(grace.InGrace); // баннер «Продлите» — повод создать новый тикет

        api.Clock.Advance(TimeSpan.FromDays(8));
        await api.RunLifecycleAsync();
        Assert.Equal(TierSlugs.Tadpole, (await EntitlementsAsync(api, user.AccessToken)).Tier);
    }

    [Fact]
    public async Task Refresh_rotation_and_reuse_detection()
    {
        var (api, user) = await StartAsync();
        var client = api.CreateClient();

        var rotated = await ApiFactory.ReadAsync<TokenPair>(await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(user.RefreshToken), FrogJson.Options));
        Assert.NotEqual(user.RefreshToken, rotated.RefreshToken);

        // Повторное использование старого токена — компрометация: отзываем всю цепочку.
        var reuse = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(user.RefreshToken), FrogJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(rotated.RefreshToken), FrogJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }
}
