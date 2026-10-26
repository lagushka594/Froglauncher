using System.Net;
using System.Net.Http;

namespace FrogLauncher.Core;

/// <summary>Общий HttpClient с поддержкой прокси из настроек.</summary>
public static class Http
{
    public static HttpClient Client { get; private set; } = Create(null);

    public static void Configure(string? proxy) => Client = Create(proxy);

    private static HttpClient Create(string? proxy)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try { handler.Proxy = new WebProxy(proxy.Trim()); handler.UseProxy = true; }
            catch (Exception ex) { Log.Warn("Неверный прокси: " + ex.Message); }
        }
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FrogLauncher/" + ApiConfig.LauncherVersion);
        return c;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var r = await Client.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync(ct);
    }
}