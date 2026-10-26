using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Frog.Api.Data;
using Frog.Api.Payments;
using Frog.Api.Services;
using Frog.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Frog.Api.Tests;

/// <summary>
/// Тестовый backend: InMemory БД, управляемое время (FakeTimeProvider), фейковый платёжный провайдер.
/// Фоновый воркер отключён — жизненный цикл подписок запускаем вручную.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // От текущего момента: JwtBearer проверяет срок токена по реальным часам.
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    private readonly string _db = "frog-" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-test-signing-key-0123456789");
        builder.UseSetting("Jwt:ServiceSigningKey", "test-service-key-test-service-key-0123456789");
        builder.UseSetting("Billing:DefaultProvider", "fake");
        builder.UseSetting("Billing:PublicBaseUrl", "https://api.test");

        builder.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<FrogDbContext>>();
            s.AddDbContext<FrogDbContext>(o => o.UseInMemoryDatabase(_db));
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.AddScoped<IPaymentProvider, FakePaymentProvider>();
            var worker = s.First(d => d.ImplementationType == typeof(SubscriptionLifecycleWorker));
            s.Remove(worker);
        });
    }

    public async Task<T> WithDbAsync<T>(Func<FrogDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<FrogDbContext>());
    }

    public async Task RunLifecycleAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BillingService>().RunLifecycleAsync(CancellationToken.None);
    }

    public async Task SeedStaffAsync(string login, string role, string password)
    {
        await WithDbAsync(async db =>
        {
            db.StaffAccounts.Add(new StaffAccount
            {
                Id = Guid.NewGuid(), Login = login, Role = role,
                PasswordHash = AccountService.HashPassword(password), CreatedAt = Clock.GetUtcNow(),
            });
            return await db.SaveChangesAsync();
        });
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, FrogJson.Options)!;
    }

    public HttpClient ClientWith(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task<TokenPair> RegisterAsync(string nick)
    {
        var resp = await CreateClient().PostAsJsonAsync("/auth/register",
            new RegisterRequest(nick, $"{nick}@test.ru", "password123"), FrogJson.Options);
        return await ReadAsync<TokenPair>(resp);
    }

    /// <summary>Отправить событие фейкового провайдера (подлинность — по заголовку X-Fake-Secret).</summary>
    public async Task<HttpResponseMessage> SendWebhookAsync(object payload, string secret = FakePaymentProvider.Secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/billing/webhook/fake")
        {
            Content = JsonContent.Create(payload, options: FrogJson.Options),
        };
        req.Headers.Add("X-Fake-Secret", secret);
        return await CreateClient().SendAsync(req);
    }
}

/// <summary>Провайдер-заглушка: вебхук = JSON с типом события и id платежа; подпись — общий секрет.</summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public const string Secret = "fake-webhook-secret";

    public string Name => "fake";
    public bool SupportsServerSideRenewal => true;
    public bool SupportsNativePlanChange => false;

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutContext ctx, CancellationToken ct) =>
        Task.FromResult(new CheckoutSession($"https://pay.test/{ctx.PaymentId}", "fp_" + ctx.PaymentId.ToString("N")));

    public Task<RenewalResult> ChargeRenewalAsync(RenewalContext ctx, CancellationToken ct) =>
        Task.FromResult(new RenewalResult("fr_" + ctx.PaymentId.ToString("N"), PaymentOutcome.Pending));

    public Task ChangePlanAsync(Subscription sub, string newTierSlug, BillingPeriod newPeriod, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CancelAsync(Subscription sub, CancellationToken ct) => Task.CompletedTask;

    public Task<WebhookResult> ParseWebhookAsync(WebhookRequest req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("X-Fake-Secret", out var s) || s != Secret)
            return Task.FromResult(WebhookResult.Rejected("bad secret"));

        var e = JsonSerializer.Deserialize<FakeEvent>(req.Body, FrogJson.Options)!;
        BillingEvent evt = e.Type == "succeeded"
            ? new PaymentSucceeded(null, null, e.ProviderPaymentId, null, "pm_saved_card", null)
            : new PaymentFailed(null, null, e.ProviderPaymentId, null, "insufficient_funds");
        return Task.FromResult(new WebhookResult(true, e.EventId, e.Type, evt));
    }

    public sealed record FakeEvent(string EventId, string Type, string ProviderPaymentId);
}
