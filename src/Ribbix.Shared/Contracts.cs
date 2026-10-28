namespace Ribbix.Shared;

// ---- Авторизация ----
public sealed record LoginRequest(string Login, string Password);
public sealed record RegisterRequest(string LauncherNick, string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType = "Bearer");

// ---- Биллинг ----
public sealed record CheckoutRequest(string TierSlug, BillingPeriod Period, string? Provider = null, string? PromoCode = null);

/// <summary>Статистика промокода (для партнёров-стримеров): оплаченные платежи и выручка после скидки.</summary>
public sealed record PromoStatDto(string Code, string? Partner, int Payments, decimal Revenue);
public sealed record CheckoutResponse(string PaymentUrl, Guid SubscriptionId, decimal Amount, string Currency, bool IsUpgrade);
public sealed record CancelResponse(DateTimeOffset? ActiveUntil);

public sealed record PaymentHistoryItem(
    Guid Id,
    string TierSlug,
    BillingPeriod Period,
    decimal Amount,
    string Currency,
    string Status,
    string Kind,
    string Provider,
    DateTimeOffset CreatedAt);

// ---- Админка (RibbixNews, audience=ribbixnews) ----
public sealed record AdminUserDto(
    Guid Id,
    string LauncherNick,
    string EmailMasked,
    string Tier,
    DateTimeOffset? PeriodEnd,
    bool IsAdmin,
    Guid? ActiveGrantId,
    DateTimeOffset CreatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record GrantRequest(Guid UserId, string TierSlug, string Reason);

/// <summary>Ручная выдача платного тарифа (покупка через тикет в Discord). Повторная выдача продлевает срок.</summary>
public sealed record GrantSubscriptionRequest(Guid UserId, string TierSlug, BillingPeriod Period, string Reason);
public sealed record RevokeSubscriptionRequest(Guid UserId, string Reason);
public sealed record SubscriptionGrantDto(Guid UserId, string Tier, DateTimeOffset? PeriodEnd);
public sealed record RevokeRequest(Guid GrantId, string Reason);

public sealed record AdminGrantDto(
    Guid Id,
    Guid UserId,
    string UserNick,
    string GrantedBy,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string Reason,
    string? RevokeReason,
    string Tier = "admin");

public sealed record StaffLoginRequest(string Login, string Password);
