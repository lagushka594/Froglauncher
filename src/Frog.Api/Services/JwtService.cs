using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Frog.Api.Data;
using Frog.Api.Options;
using Frog.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Frog.Api.Services;

public static class Audiences
{
    public const string Launcher = "froglauncher";
    public const string FrogNews = "frognews";
}

public static class FrogClaims
{
    public const string Nick = "nick";
    public const string Tier = "tier";
    public const string Entitlements = "entitlements";
    public const string TokenVersion = "tv";
    public const string Role = "role";
}

/// <summary>Результат ротации refresh-токена.</summary>
public sealed record RotationResult(Guid UserId, string RefreshToken);

/// <summary>
/// Выдача JWT и управление refresh-токенами.
/// Access: 15 минут. Refresh: 30 дней, в БД хранится только SHA-256, ротация при каждом использовании.
/// Повторное использование уже обменянного refresh-токена = компрометация → отзываем всю цепочку.
/// </summary>
public sealed class JwtService(
    FrogDbContext db,
    IOptions<JwtOptions> options,
    TimeProvider clock,
    ILogger<JwtService> log)
{
    private readonly JwtOptions _o = options.Value;
    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    public int AccessTokenSeconds => _o.AccessTokenMinutes * 60;
    public int ServiceTokenSeconds => _o.ServiceTokenMinutes * 60;

    public static SymmetricSecurityKey KeyFrom(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Ключ подписи JWT должен быть не короче 32 байт (задайте через User Secrets / env)");
        return new SymmetricSecurityKey(bytes);
    }

    /// <summary>Access-токен лаунчера: sub, nick, tier, entitlements[], tv, exp, iss, aud.</summary>
    public string CreateAccessToken(User user, EntitlementsDto ent)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(FrogClaims.Nick, user.LauncherNick),
            new(FrogClaims.Tier, ent.Tier),
            new(FrogClaims.TokenVersion, user.TokenVersion.ToString()),
        };
        claims.AddRange(ent.Entitlements.Select(e => new Claim(FrogClaims.Entitlements, e)));
        return Create(claims, Audiences.Launcher, _o.SigningKey, _o.AccessTokenMinutes);
    }

    /// <summary>Service-to-service токен для FrogNews (audience=frognews, отдельный ключ).</summary>
    public string CreateServiceToken(StaffAccount staff)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, staff.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Name, staff.Login),
            new(FrogClaims.Role, staff.Role),
        };
        return Create(claims, Audiences.FrogNews, _o.ServiceSigningKey, _o.ServiceTokenMinutes);
    }

    private string Create(IEnumerable<Claim> claims, string audience, string key, int minutes)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(minutes),
            signingCredentials: new SigningCredentials(KeyFrom(key), SecurityAlgorithms.HmacSha256));
        return Handler.WriteToken(token);
    }

    public async Task<string> IssueRefreshTokenAsync(Guid userId, string audience, Guid? familyId = null, CancellationToken ct = default)
    {
        var raw = NewRawToken();
        var now = clock.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            FamilyId = familyId ?? Guid.NewGuid(),
            Audience = audience,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_o.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Обменивает refresh-токен на новый. null — токен недействителен (нужен повторный вход).</summary>
    public async Task<RotationResult?> RotateAsync(string rawToken, string audience, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.Audience == audience, ct);
        if (token is null) return null;

        var now = clock.GetUtcNow();
        if (token.RevokedAt is not null)
        {
            // Повторное использование: кто-то сохранил старый токен. Отзываем всю семью.
            log.LogWarning("Повторное использование refresh-токена, семья {FamilyId} отозвана", token.FamilyId);
            await db.RefreshTokens
                .Where(t => t.FamilyId == token.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateCompatAsync(db, t => t.RevokedAt = now, ct);
            return null;
        }

        if (token.ExpiresAt <= now) return null;

        var newRaw = NewRawToken();
        var replacement = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = Hash(newRaw),
            FamilyId = token.FamilyId,
            Audience = audience,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_o.RefreshTokenDays),
        };
        token.RevokedAt = now;
        token.ReplacedById = replacement.Id;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync(ct);
        return new RotationResult(token.UserId, newRaw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Отзыв всех refresh-токенов + инкремент версии: access-токены перестают приниматься сразу.</summary>
    public async Task ForceReloginAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.Audience == Audiences.Launcher && t.RevokedAt == null)
            .ExecuteUpdateCompatAsync(db, t => t.RevokedAt = now, ct);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.TokenVersion++;
        await db.SaveChangesAsync(ct);
    }

    internal static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string NewRawToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
}

internal static class QueryableExtensions
{
    /// <summary>
    /// Массовое обновление. На реальной БД можно заменить на ExecuteUpdateAsync;
    /// здесь — через трекинг, чтобы работало и с InMemory-провайдером в тестах.
    /// </summary>
    public static async Task ExecuteUpdateCompatAsync<T>(this IQueryable<T> query, FrogDbContext db,
        Action<T> apply, CancellationToken ct) where T : class
    {
        foreach (var item in await query.ToListAsync(ct)) apply(item);
        await db.SaveChangesAsync(ct);
    }
}
