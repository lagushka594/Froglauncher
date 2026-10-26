using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrogNews.DevHost;

/// <summary>
/// Локальные учётки FrogNews (FrogNews.settings.json → LocalStaff) для входа без сервера.
/// Хранится только PBKDF2-SHA256 хэш пароля с солью — открытого пароля нет ни в файле, ни в exe.
/// Если backend доступен, вход идёт через него (service-токен), локальная проверка — запасной вариант.
/// </summary>
public static class LocalStaff
{
    private const int Iterations = 200_000;

    public sealed record Account(string Login, string Role, string PasswordHash);

    public static IReadOnlyList<Account> Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "FrogNews.settings.json");
        try
        {
            if (!File.Exists(file)) return Array.Empty<Account>();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("LocalStaff", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<Account>();
            return arr.EnumerateArray().Select(a => new Account(
                a.GetProperty("Login").GetString() ?? "",
                a.TryGetProperty("Role", out var r) ? r.GetString() ?? "" : "",
                a.GetProperty("PasswordHash").GetString() ?? "")).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException)
        {
            return Array.Empty<Account>();
        }
    }

    /// <summary>Роль при верном логине и пароле, иначе null.</summary>
    public static string? Verify(string login, string password)
    {
        var acc = Load().FirstOrDefault(a => string.Equals(a.Login, login.Trim(), StringComparison.OrdinalIgnoreCase));
        return acc is not null && VerifyHash(acc.PasswordHash, password) ? acc.Role : null;
    }

    /// <summary>Формат: pbkdf2-sha256$итерации$соль_base64$хэш_base64.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyHash(string stored, string password)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
