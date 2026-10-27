using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Markup;

namespace FrogLauncher.Core;

public sealed record LanguageInfo(string Code, string NativeName, string Culture);

/// <summary>
/// Локализация «по исходной строке», как gettext: ключ — русский текст, словари Lang/{код}.json — переводы.
/// Нет перевода — показываем русский. Язык выбирается при запуске (авто по системе или вручную в «Настройках»);
/// смена языка применяется после перезапуска лаунчера. Переводы сообщества — через Crowdin (crowdin.yml).
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<LanguageInfo> Languages = new[]
    {
        new LanguageInfo("ru", "Русский", "ru-RU"),
        new LanguageInfo("en", "English", "en-US"),
        new LanguageInfo("es", "Español", "es-ES"),
        new LanguageInfo("de", "Deutsch", "de-DE"),
        new LanguageInfo("pt-BR", "Português (Brasil)", "pt-BR"),
    };

    private static Dictionary<string, string> _map = new();

    /// <summary>Код активного языка: ru, en, es, de, pt-BR.</summary>
    public static string Language { get; private set; } = "ru";
    public static CultureInfo Culture { get; private set; } = new("ru-RU");

    /// <summary>Для XAML: элементы, которые есть только с русской графикой.</summary>
    public static System.Windows.Visibility RussianVisibility =>
        Language == "ru" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>"auto" → язык системы, если он поддерживается, иначе английский.</summary>
    public static string Resolve(string? setting, CultureInfo system)
    {
        if (setting is { Length: > 0 } s && s != "auto" && Languages.Any(l => l.Code == s)) return s;
        if (system.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        var two = system.TwoLetterISOLanguageName;
        // Белорусский, украинский, казахский интерфейс — чаще всего с русским в играх.
        if (two is "be" or "uk" or "kk") return "ru";
        return Languages.FirstOrDefault(l => l.Code == two)?.Code ?? "en";
    }

    /// <summary>Вызывается до создания любых окон и ресурсов (в конструкторе App).</summary>
    public static void Init(string? setting)
    {
        Language = Resolve(setting, CultureInfo.InstalledUICulture);
        Culture = new CultureInfo(Languages.First(l => l.Code == Language).Culture);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        _map = Language == "ru" ? new() : LoadDictionary(Language);
    }

    internal static Dictionary<string, string> LoadDictionary(string code)
    {
        using var s = typeof(L).Assembly.GetManifestResourceStream($"FrogLauncher.Lang.{code}.json");
        if (s is null) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s)?
                .Where(kv => kv.Value is { Length: > 0 })
                .ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>Для тестов: подставить словарь напрямую.</summary>
    internal static void Use(string code, Dictionary<string, string> map)
    {
        Language = code;
        _map = map;
    }

    /// <summary>Перевод готовой строки (литерал, сообщение сервера). Пробелы по краям сохраняются.</summary>
    public static string T(string? ru)
    {
        if (string.IsNullOrEmpty(ru) || _map.Count == 0) return ru ?? "";
        if (_map.TryGetValue(ru, out var t)) return t;
        var trimmed = ru.Trim();
        if (trimmed.Length != ru.Length && _map.TryGetValue(trimmed, out t))
            return ru[..ru.IndexOf(trimmed[0])] + t + ru[(ru.LastIndexOf(trimmed[^1]) + 1)..];
        return ru;
    }

    /// <summary>
    /// Перевод интерполированной строки: <c>L.T($"Осталось {n} дней")</c> ищется по ключу «Осталось {0} дней»,
    /// а строковые аргументы («дней») переводятся отдельно.
    /// </summary>
    public static string T([InterpolatedStringHandlerArgument] ref LocHandler text) => text.Finish();

    internal static string Format(string key, object?[] args)
    {
        var template = _map.Count > 0 && _map.TryGetValue(key, out var t) ? t : key;
        for (var i = 0; i < args.Length; i++)
            if (args[i] is string s) args[i] = T(s);
        try { return string.Format(Culture, template, args); }
        catch (FormatException) { return string.Format(Culture, key, args); }
    }
}

[InterpolatedStringHandler]
public ref struct LocHandler
{
    private readonly StringBuilder _key;
    private readonly List<object?> _args;

    public LocHandler(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _args = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string s) => _key.Append(s.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => Hole(value, null, null);
    public void AppendFormatted<T>(T value, string? format) => Hole(value, null, format);
    public void AppendFormatted<T>(T value, int alignment) => Hole(value, alignment, null);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Hole(value, alignment, format);

    private void Hole(object? value, int? alignment, string? format)
    {
        _key.Append('{').Append(_args.Count);
        if (alignment is { } a) _key.Append(',').Append(a);
        if (format is not null) _key.Append(':').Append(format);
        _key.Append('}');
        _args.Add(value);
    }

    public string Finish() => L.Format(_key.ToString(), _args.ToArray());
}

/// <summary>XAML: <c>Text="{l:T 'Играть'}"</c> — перевод подставляется при загрузке разметки.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

/// <summary>Выбор языка до запуска окон: настройки читаются напрямую из settings.json.</summary>
public static class LanguageBootstrap
{
    public static string? ReadSetting()
    {
        // Для проверки переводов без правки настроек: FROGLAUNCHER_LANG=en.
        if (Environment.GetEnvironmentVariable("FROGLAUNCHER_LANG") is { Length: > 0 } env) return env;
        try
        {
            var file = Path.Combine(LauncherPaths.Root, "settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("language", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }
}
