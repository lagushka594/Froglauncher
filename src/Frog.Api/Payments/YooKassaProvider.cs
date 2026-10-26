using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Frog.Api.Data;
using Frog.Api.Options;
using Frog.Shared;
using Microsoft.Extensions.Options;

namespace Frog.Api.Payments;

/// <summary>
/// YooKassa (РФ). Нативных подписок нет: первый платёж сохраняет способ оплаты
/// (save_payment_method), продления списываем сами через payment_method_id.
///
/// Подлинность вебхуков. YooKassa НЕ подписывает уведомления HMAC — это ограничение API.
/// Поэтому три уровня защиты:
///  1) секрет в URL вебхука (сравнение за постоянное время);
///  2) IP отправителя из официального списка YooKassa;
///  3) статус платежа берётся НЕ из тела вебхука, а повторным GET /payments/{id} с нашими ключами.
/// Подделать п.3 без секретного ключа магазина невозможно.
/// </summary>
public sealed class YooKassaProvider(
    HttpClient http,
    IOptions<YooKassaOptions> options,
    ILogger<YooKassaProvider> log) : IPaymentProvider
{
    private readonly YooKassaOptions _o = options.Value;

    // https://yookassa.ru/developers/using-api/webhooks#ip
    private static readonly IPNetwork[] AllowedNetworks =
    {
        IPNetwork.Parse("185.71.76.0/27"),
        IPNetwork.Parse("185.71.77.0/27"),
        IPNetwork.Parse("77.75.153.0/25"),
        IPNetwork.Parse("77.75.156.11/32"),
        IPNetwork.Parse("77.75.156.35/32"),
        IPNetwork.Parse("77.75.154.128/25"),
        IPNetwork.Parse("2a02:5180::/32"),
    };

    public string Name => "yookassa";
    public bool SupportsServerSideRenewal => true;
    public bool SupportsNativePlanChange => false;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutContext ctx, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["amount"] = Amount(ctx.Amount, ctx.Currency),
            ["capture"] = true,
            ["save_payment_method"] = true,
            ["confirmation"] = new JsonObject { ["type"] = "redirect", ["return_url"] = ctx.ReturnUrl },
            ["description"] = $"FrogLauncher {ctx.TierName} ({(ctx.Period == BillingPeriod.Year ? "год" : "месяц")})",
            ["metadata"] = Metadata(ctx.SubscriptionId, ctx.PaymentId, ctx.Kind),
        };

        // Ключ идемпотентности = наш PaymentId: повтор Polly не создаст второй платёж.
        var json = await SendAsync(HttpMethod.Post, "payments", body, ctx.PaymentId.ToString(), ct);
        var url = json["confirmation"]?["confirmation_url"]?.GetValue<string>()
                  ?? throw new PaymentProviderException("YooKassa не вернула confirmation_url");
        return new CheckoutSession(url, json["id"]?.GetValue<string>());
    }

    public async Task<RenewalResult> ChargeRenewalAsync(RenewalContext ctx, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["amount"] = Amount(ctx.Amount, ctx.Currency),
            ["capture"] = true,
            ["payment_method_id"] = ctx.PaymentMethodId,
            ["description"] = ctx.Description,
            ["metadata"] = Metadata(ctx.SubscriptionId, ctx.PaymentId, PaymentKind.Renewal),
        };

        var json = await SendAsync(HttpMethod.Post, "payments", body, ctx.PaymentId.ToString(), ct);
        var id = json["id"]?.GetValue<string>() ?? throw new PaymentProviderException("YooKassa не вернула id платежа");
        return new RenewalResult(id, MapStatus(json["status"]?.GetValue<string>()));
    }

    public Task ChangePlanAsync(Subscription sub, string newTierSlug, BillingPeriod newPeriod, CancellationToken ct) =>
        throw new NotSupportedException("YooKassa: апгрейд оформляется отдельным платежом с прорейтом");

    // Автопродление — наше, достаточно не списывать следующий платёж.
    public Task CancelAsync(Subscription sub, CancellationToken ct) => Task.CompletedTask;

    public async Task<WebhookResult> ParseWebhookAsync(WebhookRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_o.WebhookSecret) || req.QueryToken is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.QueryToken), Encoding.UTF8.GetBytes(_o.WebhookSecret)))
            return WebhookResult.Rejected("неверный секрет вебхука");

        if (_o.EnforceIpAllowlist && (req.RemoteIp is null || !IsAllowedIp(req.RemoteIp)))
            return WebhookResult.Rejected($"IP {req.RemoteIp} не из списка YooKassa");

        JsonNode? payload;
        try { payload = JsonNode.Parse(req.Body); }
        catch (JsonException) { return WebhookResult.Rejected("невалидный JSON"); }

        var evt = payload?["event"]?.GetValue<string>() ?? "";
        var paymentId = payload?["object"]?["id"]?.GetValue<string>();
        if (paymentId is null || !evt.StartsWith("payment.", StringComparison.Ordinal))
            return new WebhookResult(true, null, evt, null); // refund.* и прочее нам не нужно

        // Источник истины — API YooKassa, а не тело уведомления.
        var payment = await SendAsync(HttpMethod.Get, $"payments/{paymentId}", null, null, ct);
        var status = payment["status"]?.GetValue<string>();
        var meta = payment["metadata"];
        Guid? subId = Guid.TryParse(meta?["subscription_id"]?.GetValue<string>(), out var s) ? s : null;
        Guid? payId = Guid.TryParse(meta?["payment_id"]?.GetValue<string>(), out var p) ? p : null;
        var eventId = $"{paymentId}:{status}";

        BillingEvent? billingEvent = MapStatus(status) switch
        {
            PaymentOutcome.Succeeded => new PaymentSucceeded(subId, payId, paymentId,
                ProviderSubId: subId is null ? null : $"yk_{subId:N}",
                PaymentMethodId: payment["payment_method"]?["saved"]?.GetValue<bool>() == true
                    ? payment["payment_method"]?["id"]?.GetValue<string>()
                    : null,
                PeriodEnd: null),
            PaymentOutcome.Failed => new PaymentFailed(subId, payId, paymentId, null,
                payment["cancellation_details"]?["reason"]?.GetValue<string>()),
            _ => null, // waiting_for_capture / pending — ждём финального статуса
        };

        return new WebhookResult(true, eventId, evt, billingEvent);
    }

    internal static bool IsAllowedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return AllowedNetworks.Any(n => n.Contains(ip));
    }

    private static PaymentOutcome MapStatus(string? status) => status switch
    {
        "succeeded" => PaymentOutcome.Succeeded,
        "canceled" => PaymentOutcome.Failed,
        _ => PaymentOutcome.Pending,
    };

    private static JsonObject Amount(decimal amount, string currency) => new()
    {
        ["value"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
        ["currency"] = currency,
    };

    private static JsonObject Metadata(Guid subscriptionId, Guid paymentId, PaymentKind kind) => new()
    {
        ["subscription_id"] = subscriptionId.ToString(),
        ["payment_id"] = paymentId.ToString(),
        ["kind"] = kind.ToString().ToLowerInvariant(),
    };

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, string? idempotenceKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_o.ShopId) || string.IsNullOrEmpty(_o.SecretKey))
            throw new PaymentProviderException("YooKassa: не заданы ShopId/SecretKey (User Secrets / env)");

        using var msg = new HttpRequestMessage(method, new Uri(new Uri(_o.ApiBase), path));
        msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.ShopId}:{_o.SecretKey}")));
        if (idempotenceKey is not null) msg.Headers.Add("Idempotence-Key", idempotenceKey);
        if (body is not null) msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(msg, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogError("YooKassa {Method} {Path} → {Status}", method, path, (int)resp.StatusCode);
            throw new PaymentProviderException($"YooKassa вернула {(int)resp.StatusCode}");
        }

        return JsonNode.Parse(text) ?? throw new PaymentProviderException("Пустой ответ YooKassa");
    }
}
