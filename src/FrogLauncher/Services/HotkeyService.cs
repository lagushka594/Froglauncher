using FrogLauncher.Core;
using System.Windows.Input;

namespace FrogLauncher.Services;

public sealed record HotkeyAction(string Id, string Label, string Default);

/// <summary>
/// Горячие клавиши лаунчера. Сочетание хранится строкой «Ctrl+Shift+M»; игрок может переназначить его в настройках.
/// </summary>
public sealed class HotkeyService(SettingsService settings)
{
    public static readonly IReadOnlyList<HotkeyAction> Actions = new[]
    {
        new HotkeyAction("play", L.T("Играть"), "Ctrl+Enter"),
        new HotkeyAction("new", L.T("Новая версия"), "Ctrl+N"),
        new HotkeyAction("search", L.T("Командная палитра (поиск всего)"), "Ctrl+K"),
        new HotkeyAction("refresh", L.T("Обновить (версии, серверы, подписку)"), "F5"),
        new HotkeyAction("settings", L.T("Настройки"), "Ctrl+,"),
        new HotkeyAction("minimizePlay", L.T("Свернуть и играть"), "Ctrl+Shift+M"),
    };

    /// <summary>Пока игрок назначает клавишу (в настройках или управлении Minecraft) — сочетания не срабатывают.</summary>
    public static bool Suspended { get; set; }

    public string Get(string actionId) =>
        settings.Current.Hotkeys.TryGetValue(actionId, out var g) && g.Length > 0 ? g : Actions.First(a => a.Id == actionId).Default;

    public void Set(string actionId, string gesture)
    {
        // Одно сочетание — одно действие: снимаем его с других действий.
        foreach (var a in Actions.Where(a => a.Id != actionId && Get(a.Id) == gesture)) settings.Current.Hotkeys[a.Id] = "";
        settings.Current.Hotkeys[actionId] = gesture;
        settings.Save();
    }

    public void Reset()
    {
        settings.Current.Hotkeys.Clear();
        settings.Save();
    }

    public string? Match(Key key, ModifierKeys mods) =>
        Format(key, mods) is { } g ? Actions.FirstOrDefault(a => Get(a.Id) == g)?.Id : null;

    /// <summary>Клавиша + модификаторы → «Ctrl+Shift+M». Одиночные модификаторы — null.</summary>
    public static string? Format(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;

        var name = key switch
        {
            Key.Return => "Enter",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.Space => "Space",
            Key.Escape => "Esc",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        };
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(name);
        return string.Join("+", parts);
    }
}
