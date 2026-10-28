using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ribbix.Api.Data;
using Ribbix.Api.Options;
using Ribbix.Shared;
using Microsoft.Extensions.Options;

namespace Ribbix.Api.Payments;

/// <summary>
/// Paddle Billing (глобально). Подписки, продления, dunning и прорейт делает Paddle,
/// мы синхронизируем состояние по вебхукам. Цены задаются price_id в кабинете Paddle
/// и должны совпадать с tiers.json.
/// Подпись: заголовок Paddle-Signature "ts=...;h1=..." = HMAC-SHA256(secret, "{ts}:{raw_body}").
/// </summary>
public sealed class PaddleProvider(
    HttpClient http,
    IOptions<PaddleOptions> options,
    TimeProvider clock,
    ILogger<PaddleProvider> log) : IPaymentProvider
{
    private static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);
    private readonly PaddleOptions _o = options.Value;

    public string Name => "paddle";
    public bool SupportsServerSideRenewal => false;
    public bool SupportsNativePlanChange => true;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutContext ctx, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["price_id"] = PriceId(ctx.TierSlug, ctx.Period), ["quantity"] = 1 }),
            ["custom_data"] = new JsonObject
            {
                ["subscription_id"] = ctx.SubscriptionId.ToString(),
                ["payment_id"] = ctx.PaymentId.ToString(),
            },
        };

        var json = await SendAsync(HttpMethod.Post, "transactions", body, ct);
        var url = json["data"]?["checkout"]?["url"]?.GetValue<string>()
                  ?? throw new PaymentProviderException("Paddle не вернул checkout.url (проверьте Default payment link)");
        return new CheckoutSession(url, json["data"]?["id"]?.GetValue<string>());
    }

    public Task<RenewalResult> ChargeRenewalAsync(RenewalContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("Paddle продлевает подписки сам");

    public async Task ChangePlanAsync(Subscription sub, string newTierSlug, BillingPeriod newPeriod, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["price_id"] = PriceId(newTierSlug, newPeriod), ["quantity"] = 1 }),
            // Paddle сам посчитает доплату за остаток периода и спишет её сразу.
            ["proration_billing_mode"] = "prorated_immediately",
        };
        await SendAsync(HttpMethod.Patch, $"subscriptions/{RequireSubId(sub)}", body, ct);
    }

    public async Task CancelAsync(Subscription sub, CancellationToken ct)
    {
        var body = new JsonObject { ["effective_from"] = "next_billing_period" };
        await SendAsync(HttpMethod.Post, $"subscriptions/{RequireSubId(sub)}/cancel", body, ct);
    }

    public Task<WebhookResult> ParseWebhookAsync(WebhookRequest req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("Paddle-Signature", out var header) || !VerifySignature(header, req.Body))
            return Task.FromResult(WebhookResult.Rejected("неверная подпись Paddle-Signature"));

        JsonNode? root;
        try { root = JsonNode.Parse(req.Body); }
        catch (JsonException) { return Task.FromResult(WebhookResult.Rejected("невалидный JSON")); }

        var eventId = root?["event_id"]?.GetValue<string>();
        var type = root?["event_type"]?.GetValue<string>() ?? "";
        var data = root?["data"];
        if (data is null) return Task.FromResult(WebhookResult.Rejected("нет data"));

        var custom = data["custom_data"];
        Guid? subId = Guid.TryParse(custom?["subscription_id"]?.GetValue<string>(), out var s) ? s : null;
        Guid? payId = Guid.TryParse(custom?["payment_id"]?.GetValue<string>(), out var p) ? p : null;

        BillingEvent? evt = type switch
        {
            "transaction.completed" => new PaymentSucceeded(subId, payId,
                data["id"]!.GetValue<string>(),
                data["subscription_id"]?.GetValue<string>(),
                PaymentMethodId: null,
                PeriodEnd: Date(data["billing_period"]?["ends_at"])),

            "transaction.payment_failed" => new PaymentFailed(subId, payId,
                data["id"]?.GetValue<string>(), data["subscription_id"]?.GetValue<string>(), "payment_failed"),

            "subscription.created" or "subscription.updated" or "subscription.activated"
                or "subscription.past_due" or "subscription.resumed" => MapSubscription(data, subId),

            "subscription.canceled" => new SubscriptionCanceled(data["id"]!.GetValue<string>(),
                Date(data["canceled_at"]) ?? clock.GetUtcNow()),

            _ => null,
        };

        return Task.FromResult(new WebhookResult(true, eventId, type, evt));
    }

    private SubscriptionUpdated MapSubscription(JsonNode data, Guid? subId)
    {
        var priceId = data["items"]?[0]?["price"]?["id"]?.GetValue<string>();
        string? tier = null;
        BillingPeriod? period = null;
        var match = _o.Prices.FirstOrDefault(kv => kv.Value == priceId);
        if (match.Key is { } key && key.Split(':') is [var t, var per])
        {
            tier = t;
            period = per == "year" ? BillingPeriod.Year : BillingPeriod.Month;
        }

        return new SubscriptionUpdated(
            data["id"]!.GetValue<string>(), subId, tier, period,
            Date(data["current_billing_period"]?["ends_at"]),
            data["status"]?.GetValue<string>() ?? "active",
            CancelAtPeriodEnd: data["scheduled_change"]?["action"]?.GetValue<string>() == "cancel");
    }

    internal bool VerifySignature(string header, string body)
    {
        if (string.IsNullOrEmpty(_o.WebhookSecret)) return false;

        string? ts = null, h1 = null;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "ts") ts = kv[1];
            else if (kv[0] == "h1") h1 = kv[1];
        }

        if (ts is null || h1 is null || !long.TryParse(ts, out var unix)) return false;
        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix);
        if (age.Duration() > SignatureTolerance) return false; // защита от replay

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_o.WebhookSecret), Encoding.UTF8.GetBytes($"{ts}:{body}"));
        byte[] actual;
        try { actual = Convert.FromHexString(h1); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string PriceId(string tier, BillingPeriod period)
    {
        var key = $"{tier}:{(period == BillingPeriod.Year ? "year" : "month")}";
        return _o.Prices.TryGetValue(key, out var id) && !string.IsNullOrEmpty(id)
            ? id
            : throw new PaymentProviderException($"Paddle: не задан price_id для {key}");
    }

    private static string RequireSubId(Subscription sub) =>
        sub.ProviderSubId ?? throw new PaymentProviderException("Подписка ещё не синхронизирована с Paddle");

    private static DateTimeOffset? Date(JsonNode? node) =>
        node?.GetValue<string>() is { } s &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_o.ApiKey))
            throw new PaymentProviderException("Paddle: не задан ApiKey (User Secrets / env)");

        using var msg = new HttpRequestMessage(method, new Uri(new Uri(_o.ApiBase), path));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
        if (body is not null) msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(msg, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogError("Paddle {Method} {Path} → {Status}", method, path, (int)resp.StatusCode);
            throw new PaymentProviderException($"Paddle вернул {(int)resp.StatusCode}");
        }

        return JsonNode.Parse(text) ?? throw new PaymentProviderException("Пустой ответ Paddle");
    }
}
