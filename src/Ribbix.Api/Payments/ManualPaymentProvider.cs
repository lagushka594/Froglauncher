using Ribbix.Api.Data;
using Ribbix.Api.Services;
using Ribbix.Shared;

namespace Ribbix.Api.Payments;

/// <summary>
/// Ручная оплата: пользователь покупает через тикет в Discord, сотрудник выдаёт тариф в RibbixNews
/// (POST /admin/subscriptions/grant). Автосписаний нет — по окончании срока включается grace 7 дней,
/// баннер «Продлите» напоминает создать новый тикет.
/// </summary>
public sealed class ManualPaymentProvider : IPaymentProvider
{
    public const string ProviderName = "manual";

    public string Name => ProviderName;
    public bool SupportsServerSideRenewal => false;
    public bool SupportsNativePlanChange => false;

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutContext ctx, CancellationToken ct) =>
        throw new BillingException("Покупка оформляется через тикет в Discord");

    public Task<RenewalResult> ChargeRenewalAsync(RenewalContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("Ручные подписки не продлеваются автоматически");

    public Task ChangePlanAsync(Subscription sub, string newTierSlug, BillingPeriod newPeriod, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CancelAsync(Subscription sub, CancellationToken ct) => Task.CompletedTask;

    public Task<WebhookResult> ParseWebhookAsync(WebhookRequest req, CancellationToken ct) =>
        Task.FromResult(WebhookResult.Rejected("у ручного провайдера нет вебхуков"));
}
