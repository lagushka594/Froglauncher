namespace Frog.Api.Options;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "frog-api";
    /// <summary>Ключ подписи HMAC-SHA256 токенов лаунчера, минимум 32 байта. Только из секретов.</summary>
    public string SigningKey { get; set; } = "";
    /// <summary>Отдельный ключ для service-to-service токенов FrogNews.</summary>
    public string ServiceSigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int ServiceTokenMinutes { get; set; } = 15;
}

public sealed class BillingOptions
{
    public const string Section = "Billing";
    public string DefaultProvider { get; set; } = "yookassa";
    /// <summary>Публичный адрес API — для return_url после оплаты.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5001";
    public int GraceDays { get; set; } = 7;
    /// <summary>За сколько часов до конца периода списывать продление (YooKassa).</summary>
    public int RenewalLeadHours { get; set; } = 24;
    /// <summary>Сколько раз повторять неудачное списание в течение grace.</summary>
    public int MaxRenewalAttempts { get; set; } = 3;
}

public sealed class YooKassaOptions
{
    public const string Section = "YooKassa";
    public string ShopId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string ApiBase { get; set; } = "https://api.yookassa.ru/v3/";
    /// <summary>Секрет в URL вебхука (YooKassa не подписывает уведомления — см. README).</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>Проверять IP отправителя по списку YooKassa. Отключать только локально.</summary>
    public bool EnforceIpAllowlist { get; set; } = true;
}

public sealed class PaddleOptions
{
    public const string Section = "Paddle";
    public string ApiKey { get; set; } = "";
    public string ApiBase { get; set; } = "https://api.paddle.com/";
    /// <summary>Секрет notification destination — для проверки Paddle-Signature (HMAC-SHA256).</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>price_id по ключу "{tier}:{period}", например "frog_plus:month".</summary>
    public Dictionary<string, string> Prices { get; set; } = new();
}
