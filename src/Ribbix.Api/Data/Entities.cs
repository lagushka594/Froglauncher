using Ribbix.Shared;

namespace Ribbix.Api.Data;

/// <summary>Пользователь RibbixLauncher (аккаунт для подписок, не игровой аккаунт Microsoft).</summary>
public class User
{
    public Guid Id { get; set; }
    public string LauncherNick { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Версия токенов. Инкремент = все выданные access-токены сразу недействительны.</summary>
    public int TokenVersion { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();
    public List<AdminGrant> AdminGrants { get; set; } = new();
}

public enum SubscriptionStatus
{
    /// <summary>Создана при checkout, ждёт первой оплаты.</summary>
    Pending,
    Active,
    /// <summary>Не удалось продлить — идёт grace-период.</summary>
    PastDue,
    /// <summary>Автопродление отключено, доступ до конца оплаченного периода.</summary>
    Canceled,
    /// <summary>Окончательно закончилась — пользователь на tadpole.</summary>
    Expired,
    /// <summary>Заменена другой подпиской (апгрейд со сменой периода).</summary>
    Replaced,
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string TierSlug { get; set; } = TierSlugs.Tadpole;
    public BillingPeriod Period { get; set; }
    public SubscriptionStatus Status { get; set; }

    /// <summary>yookassa | paddle</summary>
    public string Provider { get; set; } = "";
    /// <summary>ID подписки у провайдера. Ключ идемпотентности вебхуков.</summary>
    public string? ProviderSubId { get; set; }
    /// <summary>Сохранённый способ оплаты (YooKassa) для рекуррентных списаний.</summary>
    public string? ProviderPaymentMethodId { get; set; }

    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset? GraceUntil { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    /// <summary>Подписка, которую эта заменяет при апгрейде (активируется после оплаты).</summary>
    public Guid? ReplacesSubscriptionId { get; set; }
    public int FailedRenewalAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }

    public List<Payment> Payments { get; set; } = new();
}

public enum PaymentKind { Initial, Renewal, Upgrade }

public class Payment
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public Guid UserId { get; set; }

    public string Provider { get; set; } = "";
    /// <summary>ID платежа/транзакции у провайдера — уникален (идемпотентность).</summary>
    public string? ProviderPaymentId { get; set; }
    public string TierSlug { get; set; } = "";
    public BillingPeriod Period { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    /// <summary>pending | succeeded | failed | refunded</summary>
    public string Status { get; set; } = "pending";
    public PaymentKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ручная выдача (провайдер manual): логин сотрудника RibbixNews.</summary>
    public string? GrantedBy { get; set; }
    /// <summary>Ручная выдача: номер тикета Discord / причина.</summary>
    public string? Note { get; set; }
}

/// <summary>Выдача Admin-тира через RibbixNews. Записи не удаляются — это журнал аудита.</summary>
public class AdminGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Логин superadmin в RibbixNews.</summary>
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string Reason { get; set; } = "";
    public string? RevokeReason { get; set; }
}

/// <summary>Кэш вычисленных прав (денормализация для быстрых ответов /entitlements).</summary>
public class EntitlementsCache
{
    public Guid UserId { get; set; }
    public string Json { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Refresh-токен: в БД только SHA-256 хэш. FamilyId — цепочка ротаций для детекта кражи.</summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    /// <summary>Пользователь лаунчера или сотрудник RibbixNews — в зависимости от Audience.</summary>
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; }
    /// <summary>ribbix | ribbixnews</summary>
    public string Audience { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

/// <summary>Обработанные события вебхуков — защита от повторной доставки.</summary>
public class WebhookEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// Сотрудник RibbixNews. Если в существующей БД RibbixNews уже есть таблица сотрудников —
/// замените StaffDirectory на чтение из неё, а эту таблицу уберите из миграции.
/// </summary>
public class StaffAccount
{
    public Guid Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>superadmin | news_admin | editor. Подписками управляет ТОЛЬКО superadmin.</summary>
    public string Role { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
