using System.IO;
using System.Text.Json;
using System.Windows;
using FrogNews.Subscriptions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FrogNews.DevHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection().AddFrogNewsSubscriptions(ResolveApi()).BuildServiceProvider();
        new MainWindow(services).Show();
    }

    /// <summary>Адрес backend: переменная FROGNEWS_API → FrogNews.settings.json рядом с exe → localhost.</summary>
    private static Uri ResolveApi()
    {
        if (Environment.GetEnvironmentVariable("FROGNEWS_API") is { Length: > 0 } env) return new Uri(env);

        var file = Path.Combine(AppContext.BaseDirectory, "FrogNews.settings.json");
        try
        {
            if (File.Exists(file) && JsonDocument.Parse(File.ReadAllText(file)).RootElement
                    .TryGetProperty("ApiBaseUrl", out var url) && url.GetString() is { Length: > 0 } s)
                return new Uri(s.EndsWith('/') ? s : s + "/");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UriFormatException) { }

        return new Uri("http://localhost:5080/");
    }
}
