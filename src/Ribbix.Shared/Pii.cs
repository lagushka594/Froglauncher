namespace Ribbix.Shared;

/// <summary>Маскирование персональных данных для логов и UI. Ники не считаются PII, email — считается.</summary>
public static class Pii
{
    /// <summary>ivan.petrov@mail.ru → i***v@m***.ru</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "—";
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var host = dot > 0 ? domain[..dot] : domain;
        var zone = dot > 0 ? domain[dot..] : "";

        string Mask(string s) => s.Length <= 2 ? s[0] + "***" : $"{s[0]}***{s[^1]}";
        return $"{Mask(local)}@{host[0]}***{zone}";
    }
}
