using System.Net;
using System.Net.Http.Json;
using Ribbix.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ribbix.Api.Tests;

public class PromoCatalogTests
{
    private static PromoCatalog Catalog() => new(new PromoFile
    {
        Events =
        {
            new PromoEvent { Id = "ny", Title = "Новый год", Percent = 40, From = "12-20", To = "01-10" },
            new PromoEvent { Id = "bf", Title = "Чёрная пятница", Percent = 50, Rule = "black-friday" },
            new PromoEvent { Id = "hw", Title = "Хэллоуин", Percent = 30, From = "10-29", To = "11-02" },
        },
        Codes =
        {
            new PromoCode { Code = "FROG2026", Percent = 40, Tiers = { TierSlugs.RibbixLegend }, ValidUntil = new DateTimeOffset(2027, 1, 10, 23, 59, 59, TimeSpan.Zero) },
            new PromoCode { Hash = PromoCatalog.HashCode("streamer15"), Percent = 15, Partner = "Кваканье TV" },
            new PromoCode { Code = "YEARLY", Percent = 60, Period = "year" },
        },
    });

    [Theory]
    [InlineData(2026, 12, 25, true)]
    [InlineData(2027, 1, 10, true)]
    [InlineData(2027, 1, 11, false)]
    [InlineData(2026, 12, 19, false)]
    public void New_year_event_wraps_around_january(int y, int m, int d, bool active) =>
        Assert.Equal(active, PromoCatalog.IsActive(Catalog().File.Events[0], new DateOnly(y, m, d)));

    [Fact]
    public void Black_friday_is_friday_after_fourth_thursday_until_cyber_monday()
    {
        Assert.Equal(new DateOnly(2026, 11, 27), PromoCatalog.BlackFriday(2026));
        Assert.Equal(new DateOnly(2027, 11, 26), PromoCatalog.BlackFriday(2027));
        var bf = Catalog().File.Events[1];
        Assert.True(PromoCatalog.IsActive(bf, new DateOnly(2026, 11, 30)));
        Assert.False(PromoCatalog.IsActive(bf, new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Best_discount_wins_and_does_not_stack()
    {
        var c = Catalog();
        var ny = new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero);
        // Акция 40% и промокод 15% — действует 40%, а не 55%.
        var d = c.Best(TierSlugs.RibbixPlus, BillingPeriod.Month, "STREAMER15", ny)!;
        Assert.Equal(40, d.Percent);
        Assert.Null(d.Code);
        // Вне акции — промокод партнёра.
        var p = c.Best(TierSlugs.RibbixPlus, BillingPeriod.Month, " streamer15 ", new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero))!;
        Assert.Equal(15, p.Percent);
        Assert.Equal("Кваканье TV", p.Partner);
        Assert.Equal(85m, p.Apply(100m));
    }

    [Fact]
    public void Code_checks_explain_why_it_does_not_apply()
    {
        var c = Catalog();
        var now = new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(PromoCheck.Ok, c.Check("frog2026", TierSlugs.RibbixLegend, BillingPeriod.Month, now, out _));
        Assert.Equal(PromoCheck.WrongTier, c.Check("FROG2026", TierSlugs.RibbixLite, BillingPeriod.Month, now, out _));
        Assert.Equal(PromoCheck.Expired, c.Check("FROG2026", TierSlugs.RibbixLegend, BillingPeriod.Month, now.AddMonths(1), out _));
        Assert.Equal(PromoCheck.WrongPeriod, c.Check("YEARLY", TierSlugs.RibbixLite, BillingPeriod.Month, now, out _));
        Assert.Equal(PromoCheck.Unknown, c.Check("NOPE", TierSlugs.RibbixLite, BillingPeriod.Month, now, out _));
    }

    [Fact]
    public void Repository_promos_json_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "promos.json");
        var c = PromoCatalog.Parse(File.ReadAllText(path));
        Assert.NotEmpty(c.File.Events);
        Assert.All(c.File.Events, e => Assert.InRange(e.Percent, 1, 99));
        Assert.All(c.File.Events, e => Assert.True(e.Rule is not null || (e.From is not null && e.To is not null), e.Id));
    }
}

public class PromoCheckoutTests
{
    [Fact]
    public async Task Checkout_applies_promo_code_and_counts_partner_stats()
    {
        var api = new ApiFactory();
        api.Promos.Codes.Add(new PromoCode { Code = "KVA20", Percent = 20, Partner = "Стример Ква" });
        var user = await api.RegisterAsync("promofrog");
        var client = api.ClientWith(user.AccessToken);

        var bad = await client.PostAsJsonAsync("/billing/checkout",
            new CheckoutRequest(TierSlugs.RibbixLite, BillingPeriod.Month, PromoCode: "WRONG"), RibbixJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var checkout = await ApiFactory.ReadAsync<CheckoutResponse>(await client.PostAsJsonAsync("/billing/checkout",
            new CheckoutRequest(TierSlugs.RibbixLite, BillingPeriod.Month, PromoCode: "kva20"), RibbixJson.Options));
        Assert.Equal(40m, checkout.Amount); // 50 ₽ − 20%

        var pid = await api.WithDbAsync(db => db.Payments.OrderByDescending(p => p.CreatedAt).Select(p => p.ProviderPaymentId).FirstAsync());
        Assert.Equal(HttpStatusCode.OK, (await api.SendWebhookAsync(new FakePaymentProvider.FakeEvent(Guid.NewGuid().ToString(), "succeeded", pid!))).StatusCode);

        await api.SeedStaffAsync("boss", "superadmin", "superadmin-password");
        var boss = await ApiFactory.ReadAsync<TokenPair>(await api.CreateClient()
            .PostAsJsonAsync("/auth/ribbixnews/login", new StaffLoginRequest("boss", "superadmin-password"), RibbixJson.Options));
        var stats = await ApiFactory.ReadAsync<List<PromoStatDto>>(await api.ClientWith(boss.AccessToken).GetAsync("/admin/promo-stats"));
        var row = Assert.Single(stats);
        Assert.Equal(("KVA20", "Стример Ква", 1, 40m), (row.Code, row.Partner, row.Payments, row.Revenue));
    }
}
