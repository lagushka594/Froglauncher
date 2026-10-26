using System.Net;
using Frog.Api.Data;
using Frog.Shared;

namespace Frog.Api.Payments;

/// <summary>
/// Платёжный провайдер. Реализации переводят свои вебхуки в нормализованные <see cref="BillingEvent"/>,
/// вся логика подписок — в BillingService и не зависит от провайдера.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>yookassa | paddle</summary>
    string Name { get; }

    /// <summary>
    /// true — у провайдера нет собственных подписок, продления списываем мы (YooKassa, сохранённая карта).
    /// false — провайдер продлевает сам и шлёт вебхуки (Paddle).
    /// </summary>
    bool SupportsServerSideRenewal { get; }

    /// <summary>Провайдер умеет менять план подписки с собственным прорейтом (Paddle).</summary>
    bool SupportsNativePlanChange { get; }

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutContext ctx, CancellationToken ct);

    /// <summary>Рекуррентное списание без участия пользователя.</summary>
    Task<RenewalResult> ChargeRenewalAsync(RenewalContext ctx, CancellationToken ct);

    Task ChangePlanAsync(Subscription sub, string newTierSlug, BillingPeriod newPeriod, CancellationToken ct);

    /// <summary>Отключить автопродление (доступ сохраняется до конца периода).</summary>
    Task CancelAsync(Subscription sub, CancellationToken ct);

    /// <summary>Проверить подлинность вебхука и разобрать его. Неподлинный → Authentic=false, событие не применяется.</summary>
    Task<WebhookResult> ParseWebhookAsync(WebhookRequest req, CancellationToken ct);
}

public sealed record CheckoutContext(
    Guid SubscriptionId,
    Guid PaymentId,
    Guid UserId,
    string TierSlug,
    string TierName,
    BillingPeriod Period,
    decimal Amount,
    string Currency,
    string ReturnUrl,
    PaymentKind Kind);

public sealed record CheckoutSession(string PaymentUrl, string? ProviderPaymentId);

public sealed record RenewalContext(
    Guid SubscriptionId,
    Guid PaymentId,
    string PaymentMethodId,
    decimal Amount,
    string Currency,
    string TierSlug,
    BillingPeriod Period,
    string Description);

public enum PaymentOutcome { Pending, Succeeded, Failed }

public sealed record RenewalResult(string ProviderPaymentId, PaymentOutcome Outcome);

public sealed record WebhookRequest(
    string Body,
    IReadOnlyDictionary<string, string> Headers,
    IPAddress? RemoteIp,
    string? QueryToken);

public sealed record WebhookResult(bool Authentic, string? EventId, string EventType, BillingEvent? Event, string? Error = null)
{
    public static WebhookResult Rejected(string error) => new(false, null, "rejected", null, error);
}

/// <summary>Нормализованные события биллинга.</summary>
public abstract record BillingEvent;

public sealed record PaymentSucceeded(
    Guid? SubscriptionId,
    Guid? PaymentId,
    string ProviderPaymentId,
    string? ProviderSubId,
    string? PaymentMethodId,
    DateTimeOffset? PeriodEnd) : BillingEvent;

public sealed record PaymentFailed(
    Guid? SubscriptionId,
    Guid? PaymentId,
    string? ProviderPaymentId,
    string? ProviderSubId,
    string? Reason) : BillingEvent;

public sealed record SubscriptionCanceled(string ProviderSubId, DateTimeOffset? EffectiveAt) : BillingEvent;

public sealed record SubscriptionUpdated(
    string ProviderSubId,
    Guid? SubscriptionId,
    string? TierSlug,
    BillingPeriod? Period,
    DateTimeOffset? PeriodEnd,
    string Status,
    bool CancelAtPeriodEnd) : BillingEvent;

public sealed class PaymentProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Выбор провайдера по имени.</summary>
public sealed class PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
{
    private readonly Dictionary<string, IPaymentProvider> _map =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IPaymentProvider Get(string name) =>
        _map.TryGetValue(name, out var p) ? p : throw new PaymentProviderException($"Неизвестный провайдер {name}");

    public bool TryGet(string name, out IPaymentProvider provider) => _map.TryGetValue(name, out provider!);
}
