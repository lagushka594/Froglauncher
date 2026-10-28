using Ribbix.Api.Data;
using Ribbix.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ribbix.Api.Services;

/// <summary>
/// Логин/регистрация пользователей лаунчера и сотрудников RibbixNews.
/// Если в существующем backend уже есть /auth/login — перенесите сюда только выдачу
/// tier/entitlements в токене и ротацию refresh, остальное оставьте своим.
/// </summary>
public sealed class AccountService(
    RibbixDbContext db,
    JwtService jwt,
    EntitlementService entitlements,
    TimeProvider clock)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Owner = new();

    public static string HashPassword(string password) => Hasher.HashPassword(Owner, password);

    private static bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Owner, hash, password) != PasswordVerificationResult.Failed;

    public async Task<TokenPair> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        var nick = req.LauncherNick.Trim();
        var email = req.Email.Trim().ToLowerInvariant();
        if (nick.Length is < 3 or > 16 || !nick.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new BillingException("Ник: 3–16 символов, латиница, цифры и _");
        if (!email.Contains('@') || req.Password.Length < 8)
            throw new BillingException("Проверьте email и пароль (минимум 8 символов)");
        if (await db.Users.AnyAsync(u => u.LauncherNick == nick || u.Email == email, ct))
            throw new BillingException("Ник или email уже заняты", 409);

        var user = new User
        {
            Id = Guid.NewGuid(),
            LauncherNick = nick,
            Email = email,
            PasswordHash = HashPassword(req.Password),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, null, ct);
    }

    public async Task<TokenPair?> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var login = req.Login.Trim();
        var lower = login.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.LauncherNick == login || u.Email == lower, ct);
        if (user is null || !Verify(user.PasswordHash, req.Password)) return null;
        return await IssueAsync(user, null, ct);
    }

    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var rotated = await jwt.RotateAsync(refreshToken, Audiences.Launcher, ct);
        if (rotated is null) return null;
        var user = await db.Users.FirstAsync(u => u.Id == rotated.UserId, ct);
        var ent = await entitlements.ResolveAsync(user.Id, ct);
        return new TokenPair(jwt.CreateAccessToken(user, ent), rotated.RefreshToken, jwt.AccessTokenSeconds);
    }

    private async Task<TokenPair> IssueAsync(User user, Guid? family, CancellationToken ct)
    {
        var ent = await entitlements.RefreshCacheAsync(user.Id, ct);
        var refresh = await jwt.IssueRefreshTokenAsync(user.Id, Audiences.Launcher, family, ct);
        return new TokenPair(jwt.CreateAccessToken(user, ent), refresh, jwt.AccessTokenSeconds);
    }

    // ---- Сотрудники RibbixNews (service-to-service, audience=ribbixnews) ----

    public async Task<TokenPair?> StaffLoginAsync(StaffLoginRequest req, CancellationToken ct)
    {
        var staff = await db.StaffAccounts.FirstOrDefaultAsync(s => s.Login == req.Login.Trim(), ct);
        if (staff is null || !Verify(staff.PasswordHash, req.Password)) return null;
        var refresh = await jwt.IssueRefreshTokenAsync(staff.Id, Audiences.RibbixNews, null, ct);
        return new TokenPair(jwt.CreateServiceToken(staff), refresh, jwt.ServiceTokenSeconds);
    }

    public async Task<TokenPair?> StaffRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var rotated = await jwt.RotateAsync(refreshToken, Audiences.RibbixNews, ct);
        if (rotated is null) return null;
        var staff = await db.StaffAccounts.FirstOrDefaultAsync(s => s.Id == rotated.UserId, ct);
        return staff is null ? null : new TokenPair(jwt.CreateServiceToken(staff), rotated.RefreshToken, jwt.ServiceTokenSeconds);
    }
}
