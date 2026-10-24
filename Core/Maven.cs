namespace FrogLauncher.Core;

public static class Maven
{
    /// <summary>group:artifact:version[:classifier][@ext] → относительный путь в репозитории.</summary>
    public static string ToPath(string name)
    {
        var ext = "jar";
        var n = name;
        var at = n.IndexOf('@');
        if (at >= 0) { ext = n[(at + 1)..]; n = n[..at]; }
        var p = n.Split(':');
        if (p.Length < 3) throw new FormatException("Неверное имя библиотеки: " + name);
        var cls = p.Length > 3 ? "-" + p[3] : "";
        return $"{p[0].Replace('.', '/')}/{p[1]}/{p[2]}/{p[1]}-{p[2]}{cls}.{ext}";
    }

    /// <summary>Ключ без версии — для удаления дублей при слиянии version.json.</summary>
    public static string Key(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var p = name.Split(':');
        if (p.Length < 3) return name;
        return p.Length > 3 ? $"{p[0]}:{p[1]}:{p[3]}" : $"{p[0]}:{p[1]}";
    }
}
