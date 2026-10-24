using System.Reflection;

namespace FrogLauncher.Core;

/// <summary>
/// Ключи, «зашитые» в exe при сборке (keys.props → MSBuild → AssemblyMetadata).
/// Благодаря этому пользователю ничего настраивать не нужно: открыл лаунчер — Modrinth, CurseForge и вход Microsoft работают.
/// Значения из «Настроек» (если заполнены) имеют приоритет.
/// </summary>
public static class BuiltIn
{
    private static string Meta(string key) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

    public static string CurseForgeKey => Meta("CurseForgeKey");
    public static string MsClientId => Meta("MsClientId");
    public static string NewsUrl => Meta("NewsUrl");
    public static string UpdateRepo => Meta("UpdateRepo");
}
