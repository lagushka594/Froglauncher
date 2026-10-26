using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Уровни привилегий (по возрастанию). Admin выдаётся только вручную и включает всё.</summary>
public enum Tier { None = 0, Tadpole = 1, Froglet = 2, FrogPlus = 3, Legacy = 4, Admin = 5 }

public sealed record TierDef(Tier Tier, string Id, string Name, int PriceRub, string Color, string Theme);

public static class TierTable
{
    /// <summary>Покупаемые привилегии (цены разовые, в рублях). Admin сюда не входит.</summary>
    public static readonly TierDef[] Purchasable =
    {
        new(Tier.Tadpole,  "tadpole",  "Tadpole",     50,  "#8EF5C6", "Mint"),
        new(Tier.Froglet,  "froglet",  "Froglet",     150, "#FF9A5A", "Sunset"),
        new(Tier.FrogPlus, "frogplus", "Frog+",       300, "#FF6BD6", "Aurora"),
        new(Tier.Legacy,   "legacy",   "Frog Legacy", 700, "#FFD54A", "Gold"),
    };

    public static string NameOf(Tier t) => t switch
    {
        Tier.Admin => "Admin",
        _ => Purchasable.FirstOrDefault(d => d.Tier == t)?.Name ?? ""
    };

    public static string ColorOf(Tier t) => t switch
    {
        Tier.Admin => "#FF5C5C",
        _ => Purchasable.FirstOrDefault(d => d.Tier == t)?.Color ?? "#8FA89A"
    };

    public static Tier Parse(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "tadpole" => Tier.Tadpole, "froglet" => Tier.Froglet, "frogplus" or "frog+" => Tier.FrogPlus,
        "legacy" or "froglegacy" or "frog legacy" => Tier.Legacy, "admin" => Tier.Admin, _ => Tier.None
    };

    /// <summary>Эксклюзивные темы оформления: тема открывается с соответствующим уровнем.</summary>
    public static IEnumerable<string> ThemesFor(Tier t)
    {
        foreach (var d in Purchasable) if (t == Tier.Admin || t >= d.Tier) yield return d.Theme;
    }
}

public sealed class PrivilegeGrant
{
    public string Key { get; set; } = "";       // uuid:<hex> или nick:<ник в нижнем регистре>
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Since { get; set; } = "";
    public string? Expires { get; set; }

    public string TierName => TierTable.NameOf(TierTable.Parse(Tier));
    public string ExpiresText => string.IsNullOrWhiteSpace(Expires) ? "∞" : Expires!;
    public bool IsExpired => DateTime.TryParse(Expires, out var d) && d.Date < DateTime.Today;
}

/// <summary>
/// Привилегии лежат в privileges.json рядом с news.json (тот же Gist). Выдаёт и отзывает их консоль NewsPublisher.
/// Привязка: по UUID Microsoft-аккаунта (надёжно) или по нику (удобно, но ник можно подделать в оффлайн-режиме).
/// </summary>
public sealed class PrivilegeService
{
    private static string CachePath => Path.Combine(AppPaths.Data, "privileges-cache.json");

    /// <summary>Из ссылки на news.json получаем ссылку на privileges.json.</summary>
    public static string UrlFromNews(string newsUrl)
    {
        if (string.IsNullOrWhiteSpace(newsUrl)) return "";
        var i = newsUrl.IndexOf("news.json", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? "" : newsUrl[..i] + "privileges.json" + newsUrl[(i + "news.json".Length)..];
    }

    public async Task<List<PrivilegeGrant>> FetchAsync(string newsUrl, CancellationToken ct)
    {
        string? text = null;
        var url = UrlFromNews(newsUrl);
        if (url != "")
        {
            try
            {
                var u = url + (url.Contains('?') ? "&" : "?") + "cb=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var bytes = await Http.Client.GetByteArrayAsync(u, ct);
                text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                File.WriteAllText(CachePath, text, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Привилегии: " + ex.Message); }
        }
        if (text == null && File.Exists(CachePath)) text = File.ReadAllText(CachePath, System.Text.Encoding.UTF8);

        var list = new List<PrivilegeGrant>();
        if (text == null) return list;
        try
        {
            var json = JsonNode.Parse(text)!;
            foreach (var g in json["grants"]!.AsArray())
                list.Add(new PrivilegeGrant
                {
                    Key = ((string?)g!["key"] ?? "").ToLowerInvariant(), Name = (string?)g["name"] ?? "",
                    Tier = (string?)g["tier"] ?? "", Since = (string?)g["since"] ?? "", Expires = (string?)g["expires"]
                });
        }
        catch (Exception ex) { Log.Warn("privileges.json повреждён: " + ex.Message); }
        return list;
    }

    /// <summary>Лучший действующий уровень для аккаунта.</summary>
    public static Tier Resolve(IEnumerable<PrivilegeGrant> grants, Account? acc)
    {
        if (acc == null) return Tier.None;
        var uuid = "uuid:" + acc.Uuid.Replace("-", "").ToLowerInvariant();
        var nick = "nick:" + acc.Username.ToLowerInvariant();
        var best = Tier.None;
        foreach (var g in grants)
        {
            if (g.IsExpired) continue;
            bool match = (acc.IsMicrosoft && g.Key == uuid) || g.Key == nick;
            if (!match) continue;
            var t = TierTable.Parse(g.Tier);
            if (t > best) best = t;
        }
        return best;
    }
}
