using System.Net;
using System.Security.Cryptography;
using System.Text;
using Frog.Api.Options;
using Frog.Api.Payments;
using Frog.Api.Services;
using Frog.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Frog.Api.Tests;

public class TierCatalogTests
{
    private static TierCatalog Load() =>
        TierCatalog.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "tiers.json"));

    [Fact]
    public void Public_tiers_hide_admin()
    {
        var c = Load();
        Assert.DoesNotContain(c.Public, t => t.Slug == TierSlugs.Admin);
        Assert.Contains(c.All, t => t.Slug == TierSlugs.Admin && t.Hidden);
        Assert.False(c.IsPurchasable(TierSlugs.Admin));
        Assert.False(c.IsPurchasable(TierSlugs.Tadpole));
    }

    [Fact]
    public void Tier_references_are_expanded()
    {
        var c = Load();
        var pro = c.Get(TierSlugs.FrogPro)!;
        Assert.Contains(EntitlementKeys.InstancesUnlimited, pro.Entitlements); // унаследовано от frog_plus
        Assert.Contains(EntitlementKeys.CrashAnalytics, pro.Entitlements);
        Assert.DoesNotContain(TierSlugs.FrogPlus, pro.Entitlements);         // сам слаг в списке фич не остаётся

        var admin = c.Get(TierSlugs.Admin)!;
        Assert.Contains(EntitlementKeys.ExclusiveTheme, admin.Entitlements);
        Assert.Contains(EntitlementKeys.AdminBadge, admin.Entitlements);
    }

    [Fact]
    public void Prices_are_within_rules()
    {
        foreach (var t in Load().Public.Where(t => t.PriceMonth > 0))
        {
            Assert.InRange(t.PriceMonth!.Value, 50, 700);
            Assert.Equal(t.PriceMonth * 10, t.PriceYear);
        }
    }

    [Fact]
    public void Invalid_price_fails_fast()
    {
        const string json = """
            { "tiers": [
              { "slug": "tadpole", "name": "T", "price_month": 0, "price_year": 0, "color": "#000", "entitlements": ["base"] },
              { "slug": "x", "name": "X", "price_month": 900, "price_year": 9000, "color": "#000", "entitlements": ["base"] } ] }
            """;
        Assert.Throws<InvalidOperationException>(() => TierCatalog.Parse(json));
    }
}

public class ProrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Same_period_upgrade_charges_difference_for_remaining_time()
    {
        // Froglet (50) куплен 1-го, кончается 1 ноября; 15 октября апгрейд на Frog+ (149).
        var end = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var q = ProrationCalculator.Quote(50, BillingPeriod.Month, end, 149, BillingPeriod.Month, Now);

        Assert.True(q.KeepsPeriodEnd);
        Assert.Equal(end, q.NewPeriodEnd);
        var expected = Math.Round(99m * (decimal)((end - Now).TotalSeconds / (end - end.AddMonths(-1)).TotalSeconds), 2);
        Assert.Equal(expected, q.Amount);
        Assert.InRange(q.Amount, 50, 60);
    }

    [Fact]
    public void Month_to_year_credits_unused_time()
    {
        var end = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var q = ProrationCalculator.Quote(149, BillingPeriod.Month, end, 1490, BillingPeriod.Year, Now);
        Assert.False(q.KeepsPeriodEnd);
        Assert.Equal(Now.AddYears(1), q.NewPeriodEnd);
        Assert.InRange(q.Amount, 1490 - 149, 1490 - 1);
    }

    [Fact]
    public void Charge_never_below_minimum()
    {
        var q = ProrationCalculator.Quote(50, BillingPeriod.Month, Now.AddSeconds(5), 149, BillingPeriod.Month, Now);
        Assert.Equal(ProrationCalculator.MinCharge, q.Amount);
    }
}

public class SecurityTests
{
    [Theory]
    [InlineData("ivan.petrov@mail.ru", "i***v@m***.ru")]
    [InlineData("ab@x.io", "a***@x***.io")]
    [InlineData(null, "—")]
    public void Email_is_masked(string? email, string expected) => Assert.Equal(expected, Pii.MaskEmail(email));

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("обычная причина", "обычная причина")]
    [InlineData("a,b", "\"a,b\"")]
    public void Csv_is_escaped_against_formula_injection(string input, string expected) =>
        Assert.Equal(expected, AdminService.Csv(input));

    [Theory]
    [InlineData("185.71.76.10", true)]
    [InlineData("77.75.156.11", true)]
    [InlineData("8.8.8.8", false)]
    public void YooKassa_ip_allowlist(string ip, bool allowed) =>
        Assert.Equal(allowed, YooKassaProvider.IsAllowedIp(IPAddress.Parse(ip)));

    [Fact]
    public void Paddle_signature_is_verified_and_replay_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var p = new PaddleProvider(new HttpClient(),
            Microsoft.Extensions.Options.Options.Create(new PaddleOptions { WebhookSecret = "pdl_ntfset_secret" }),
            clock, NullLogger<PaddleProvider>.Instance);

        const string body = """{"event_id":"evt_1","event_type":"transaction.completed","data":{}}""";
        var ts = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        var h1 = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("pdl_ntfset_secret"), Encoding.UTF8.GetBytes($"{ts}:{body}"))).ToLower();

        Assert.True(p.VerifySignature($"ts={ts};h1={h1}", body));
        Assert.False(p.VerifySignature($"ts={ts};h1={h1}", body + " "));           // тело изменено
        Assert.False(p.VerifySignature($"ts={ts};h1=deadbeef", body));             // чужая подпись
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(p.VerifySignature($"ts={ts};h1={h1}", body));                 // replay старого события
    }
}
