using FrogLauncher.Core;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace FrogLauncher.Services;

/// <summary>
/// options.txt Minecraft (формат key:value). Сохраняем все строки и их порядок —
/// меняем только то, что пользователь трогал, остальное (в т.ч. ключи модов) остаётся как было.
/// Если файла ещё нет (игра не запускалась), создаём его: Minecraft сам дозаполнит недостающие ключи.
/// </summary>
public sealed class OptionsFile
{
    private readonly List<KeyValuePair<string, string>> _lines = new();

    public string Path { get; }
    public bool Existed { get; }

    private OptionsFile(string path, bool existed)
    {
        Path = path;
        Existed = existed;
    }

    public static OptionsFile Load(string gameDir)
    {
        var path = System.IO.Path.Combine(gameDir, "options.txt");
        var file = new OptionsFile(path, File.Exists(path));
        if (!file.Existed) return file;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            file._lines.Add(new(line[..i], line[(i + 1)..]));
        }
        return file;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllLines(Path, _lines.Select(l => $"{l.Key}:{l.Value}"), new UTF8Encoding(false));
    }

    public IEnumerable<KeyValuePair<string, string>> All => _lines;

    public bool Has(string key) => _lines.Any(l => l.Key == key);

    public string? Get(string key) => _lines.FirstOrDefault(l => l.Key == key).Value;

    /// <summary>Строковое значение без кавычек: с 1.19 часть ключей пишется как renderClouds:"true".</summary>
    public string? GetString(string key) => Get(key) is { } v && v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : Get(key);

    public void Set(string key, string value)
    {
        var i = _lines.FindIndex(l => l.Key == key);
        if (i >= 0) _lines[i] = new(key, value);
        else _lines.Add(new(key, value));
    }

    /// <summary>Записать строку, сохранив стиль файла: была в кавычках — останется в кавычках.</summary>
    public void SetString(string key, string value)
    {
        var old = Get(key);
        Set(key, old is { Length: >= 2 } && old[0] == '"' && old[^1] == '"' ? $"\"{value}\"" : value);
    }

    public bool GetBool(string key, bool def) => Get(key) is { } v ? v == "true" : def;
    public void SetBool(string key, bool v) => Set(key, v ? "true" : "false");

    public int GetInt(string key, int def) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    public void SetInt(string key, int v) => Set(key, v.ToString(CultureInfo.InvariantCulture));

    public double GetDouble(string key, double def) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    public void SetDouble(string key, double v) => Set(key, Math.Round(v, 4).ToString("0.0###", CultureInfo.InvariantCulture));

    /// <summary>resourcePacks:["vanilla","file/Faithful.zip"] — последний в списке имеет наивысший приоритет.</summary>
    public List<string> GetResourcePacks()
    {
        try
        {
            return JsonNode.Parse(Get("resourcePacks") ?? "[]")?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public void SetResourcePacks(IEnumerable<string> packs) =>
        Set("resourcePacks", JsonSerializer.Serialize(packs.ToList()));
}

/// <summary>Вариант выбора для ComboBox: значение + подпись.</summary>
public sealed record Choice(string Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Перевод значений Minecraft ↔ понятные пользователю.</summary>
public static class McValues
{
    /// <summary>В options.txt FOV хранится как (fov − 70) / 40, диапазон −1…1 = 30…110°.</summary>
    public static int FovFromOption(double v) => (int)Math.Round(70 + v * 40);
    public static double FovToOption(int fov) => (Math.Clamp(fov, 30, 110) - 70) / 40.0;

    /// <summary>maxFps 260 = «без ограничения».</summary>
    public const int UnlimitedFps = 260;

    public static string RenderDistanceHint(int chunks) => L.T($"{chunks} чанков ≈ {chunks * 16} блоков");

    /// <summary>Облака: true = красивые, fast = быстрые, false = выкл.</summary>
    public static readonly string[] Clouds = { "false", "fast", "true" };

    public static readonly (string Key, string Label)[] SoundCategories =
    {
        ("soundCategory_master", L.T("Общая громкость")),
        ("soundCategory_music", L.T("Музыка")),
        ("soundCategory_block", L.T("Блоки")),
        ("soundCategory_weather", L.T("Погода")),
        ("soundCategory_hostile", L.T("Враждебные мобы")),
        ("soundCategory_neutral", L.T("Дружелюбные мобы")),
        ("soundCategory_player", L.T("Игроки")),
        ("soundCategory_ambient", L.T("Окружение")),
        ("soundCategory_record", L.T("Проигрыватели")),
        ("soundCategory_voice", L.T("Голос/речь")),
    };

    public static readonly Choice[] Languages =
    {
        new("ru_ru", L.T("Русский")), new("en_us", "English (US)"), new("uk_ua", L.T("Українська")), new("be_by", L.T("Беларуская")),
        new("kk_kz", L.T("Қазақша")), new("de_de", "Deutsch"), new("es_es", "Español"), new("pt_br", "Português (BR)"),
        new("fr_fr", "Français"), new("pl_pl", "Polski"), new("tr_tr", "Türkçe"), new("zh_cn", "简体中文"),
    };

    public static readonly Choice[] GarbageCollectors =
    {
        new("g1", L.T("G1GC — универсальный (по умолчанию)")),
        new("zgc", L.T("ZGC — минимальные фризы, нужна Java 17+")),
        new("parallel", L.T("Parallel — для слабых ПК, редкие длинные паузы")),
    };

    /// <summary>Real-time не предлагаем: Windows может перестать отвечать, пока игра грузит процессор.</summary>
    public static readonly Choice[] Priorities =
    {
        new("normal", L.T("Обычный")), new("abovenormal", L.T("Выше обычного")), new("high", L.T("Высокий")),
    };

    /// <summary>Основные действия управления (ключи key_key.* из options.txt).</summary>
    public static readonly (string Key, string Label)[] KeyActions =
    {
        ("key_key.forward", L.T("Вперёд")), ("key_key.back", L.T("Назад")), ("key_key.left", L.T("Влево")), ("key_key.right", L.T("Вправо")),
        ("key_key.jump", L.T("Прыжок")), ("key_key.sneak", L.T("Присесть")), ("key_key.sprint", L.T("Бег")),
        ("key_key.inventory", L.T("Инвентарь")), ("key_key.drop", L.T("Выбросить предмет")), ("key_key.swapOffhand", L.T("Во вторую руку")),
        ("key_key.chat", L.T("Чат")), ("key_key.command", L.T("Команда")), ("key_key.playerlist", L.T("Список игроков")),
        ("key_key.attack", L.T("Атака/разрушить")), ("key_key.use", L.T("Использовать")), ("key_key.pickItem", L.T("Выбрать блок")),
        ("key_key.togglePerspective", L.T("Вид от 3-го лица")), ("key_key.screenshot", L.T("Скриншот")), ("key_key.fullscreen", L.T("Полный экран")),
    };

    /// <summary>Значения по умолчанию Minecraft — если в options.txt ещё нет привязки.</summary>
    public static readonly Dictionary<string, string> DefaultKeys = new()
    {
        ["key_key.forward"] = "key.keyboard.w", ["key_key.back"] = "key.keyboard.s", ["key_key.left"] = "key.keyboard.a",
        ["key_key.right"] = "key.keyboard.d", ["key_key.jump"] = "key.keyboard.space", ["key_key.sneak"] = "key.keyboard.left.shift",
        ["key_key.sprint"] = "key.keyboard.left.control", ["key_key.inventory"] = "key.keyboard.e", ["key_key.drop"] = "key.keyboard.q",
        ["key_key.swapOffhand"] = "key.keyboard.f", ["key_key.chat"] = "key.keyboard.t", ["key_key.command"] = "key.keyboard.slash",
        ["key_key.playerlist"] = "key.keyboard.tab", ["key_key.attack"] = "key.mouse.left", ["key_key.use"] = "key.mouse.right",
        ["key_key.pickItem"] = "key.mouse.middle", ["key_key.togglePerspective"] = "key.keyboard.f5",
        ["key_key.screenshot"] = "key.keyboard.f2", ["key_key.fullscreen"] = "key.keyboard.f11",
    };

    /// <summary>Клавиша WPF → имя клавиши Minecraft (GLFW): key.keyboard.w, key.keyboard.left.shift…</summary>
    public static string? FromWpfKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return "key.keyboard." + key.ToString().ToLowerInvariant();
        if (key is >= Key.D0 and <= Key.D9) return "key.keyboard." + (key - Key.D0);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "key.keyboard.keypad." + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F12) return "key.keyboard.f" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "key.keyboard.space", Key.Tab => "key.keyboard.tab", Key.Enter => "key.keyboard.enter",
            Key.Back => "key.keyboard.backspace", Key.CapsLock => "key.keyboard.caps.lock",
            Key.LeftShift => "key.keyboard.left.shift", Key.RightShift => "key.keyboard.right.shift",
            Key.LeftCtrl => "key.keyboard.left.control", Key.RightCtrl => "key.keyboard.right.control",
            Key.LeftAlt => "key.keyboard.left.alt", Key.RightAlt => "key.keyboard.right.alt",
            Key.Up => "key.keyboard.up", Key.Down => "key.keyboard.down", Key.Left => "key.keyboard.left", Key.Right => "key.keyboard.right",
            Key.OemQuestion => "key.keyboard.slash", Key.OemTilde => "key.keyboard.grave.accent", Key.OemMinus => "key.keyboard.minus",
            Key.OemPlus => "key.keyboard.equal", Key.OemComma => "key.keyboard.comma", Key.OemPeriod => "key.keyboard.period",
            Key.OemSemicolon => "key.keyboard.semicolon", Key.OemQuotes => "key.keyboard.apostrophe",
            Key.OemOpenBrackets => "key.keyboard.left.bracket", Key.OemCloseBrackets => "key.keyboard.right.bracket",
            Key.OemPipe => "key.keyboard.backslash", Key.Insert => "key.keyboard.insert", Key.Delete => "key.keyboard.delete",
            Key.Home => "key.keyboard.home", Key.End => "key.keyboard.end", Key.PageUp => "key.keyboard.page.up",
            Key.PageDown => "key.keyboard.page.down",
            _ => null,
        };
    }

    public static string? FromMouse(MouseButton b) => b switch
    {
        MouseButton.Left => "key.mouse.left", MouseButton.Right => "key.mouse.right", MouseButton.Middle => "key.mouse.middle",
        MouseButton.XButton1 => "key.mouse.4", MouseButton.XButton2 => "key.mouse.5",
        _ => null,
    };

    /// <summary>key.keyboard.left.shift → «Left Shift», key.mouse.left → «ЛКМ».</summary>
    public static string KeyLabel(string mc) => mc switch
    {
        "key.mouse.left" => L.T("ЛКМ"), "key.mouse.right" => L.T("ПКМ"), "key.mouse.middle" => L.T("Колёсико"),
        "key.keyboard.unknown" => "—",
        _ when mc.StartsWith("key.keyboard.", StringComparison.Ordinal) =>
            string.Join(' ', mc["key.keyboard.".Length..].Split('.').Select(p => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..])),
        _ when mc.StartsWith("key.mouse.", StringComparison.Ordinal) => L.T("Мышь ") + mc["key.mouse.".Length..],
        _ => mc,
    };
}
