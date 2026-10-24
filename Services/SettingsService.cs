using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Настройки лаунчера (JSON) и сессия аккаунта (шифруется DPAPI, привязка к пользователю Windows).</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex) { Log.Error("Не удалось прочитать настройки", ex); Settings = new(); }
        AppPaths.GameRoot = Settings.GameRoot ?? "";
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Settings, Json));
        }
        catch (Exception ex) { Log.Error("Не удалось сохранить настройки", ex); }
    }

    public Account? LoadAccount()
    {
        try
        {
            if (!File.Exists(AppPaths.AccountFile)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.AccountFile), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Account>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) { Log.Warn("Сессия не прочитана: " + ex.Message); return null; }
    }

    public void SaveAccount(Account? acc)
    {
        try
        {
            if (acc == null) { if (File.Exists(AppPaths.AccountFile)) File.Delete(AppPaths.AccountFile); return; }
            Directory.CreateDirectory(AppPaths.Data);
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(acc));
            File.WriteAllBytes(AppPaths.AccountFile, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) { Log.Error("Не удалось сохранить сессию", ex); }
    }
}
