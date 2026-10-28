using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RibbixLauncher.Core;

namespace RibbixLauncher.Services;

public sealed record RecapVersion(string Name, TimeSpan Time, int Sessions, double Share);

/// <summary>Итоги года: всё — из сессий лаунчера за этот год, кроме счётчиков миров (они в Minecraft общие за всё время).</summary>
public sealed record YearRecap(
    int Year,
    TimeSpan Total,
    int Sessions,
    int DaysPlayed,
    RecapVersion? Favorite,
    int VersionsPlayed,
    DateOnly? BestDay,
    TimeSpan BestDayTime,
    TimeSpan Longest,
    IReadOnlyList<TimeSpan> Months,
    int BestStreak,
    string Rhythm,
    string? FavoriteServer,
    int Screenshots,
    McStats Mc,
    int Advancements,
    PlayerBadge Badge)
{
    public bool IsEmpty => Sessions == 0;
    public int BestMonth => Months.Count == 0 ? 0 : Months.Select((t, i) => (t, i)).MaxBy(x => x.t).i;
}

/// <summary>
/// «Мой год в Minecraft» — серия вертикальных карточек 1080×1920 в духе годовых итогов: часы, любимая версия,
/// самый активный день, месяцы, ритм игры, счётчики миров, бейдж. Рендер — WPF, как у карточек скриншотов.
/// Никаких выдуманных сравнений с другими игроками: только данные этого компьютера.
/// </summary>
public static class YearRecapService
{
    public const int Width = ShareCardService.Width, Height = ShareCardService.Height;
    public const double MarathonKm = 42.195;

    public static YearRecap Compute(int year, IReadOnlyList<PlaySession> all, IReadOnlyDictionary<string, string> names,
        IEnumerable<DateTime> screenshots, McStats mc, int advancements)
    {
        var from = new DateTime(year, 1, 1);
        var to = from.AddYears(1);
        var sessions = all.Where(s => s.Start.LocalDateTime >= from && s.Start.LocalDateTime < to).ToList();
        var total = TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks));

        var daily = PlayStatsService.Daily(sessions, (to - from).Days, DateOnly.FromDateTime(to.AddDays(-1)));
        var played = daily.Where(d => d.Time > TimeSpan.Zero).ToList();
        var best = played.Count > 0 ? played.MaxBy(d => d.Time) : null;

        var months = Enumerable.Range(1, 12)
            .Select(m => TimeSpan.FromTicks(daily.Where(d => d.Day.Month == m).Sum(d => d.Time.Ticks))).ToList();

        var versions = sessions.GroupBy(s => s.InstanceId)
            .Select(g => new RecapVersion(names.GetValueOrDefault(g.Key) ?? g.First().InstanceName,
                TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)), g.Count(), 0))
            .OrderByDescending(v => v.Time).ToList();
        var fav = versions.FirstOrDefault() is { } f && total > TimeSpan.Zero ? f with { Share = f.Time / total } : null;

        // Самая длинная серия дней подряд внутри года.
        int streak = 0, bestStreak = 0;
        foreach (var d in daily) bestStreak = Math.Max(bestStreak, streak = d.Time > TimeSpan.Zero ? streak + 1 : 0);

        var server = sessions.Where(s => !string.IsNullOrEmpty(s.Server)).GroupBy(s => s.Server!)
            .OrderByDescending(g => g.Sum(s => s.Duration.Ticks)).FirstOrDefault()?.Key;

        var hours = Math.Max(total.TotalHours, mc.PlayTime.TotalHours);
        return new YearRecap(year, total, sessions.Count, played.Count, fav, versions.Count,
            best?.Day, best?.Time ?? TimeSpan.Zero, sessions.Count > 0 ? sessions.Max(s => s.Duration) : TimeSpan.Zero,
            months, bestStreak, Rhythm(sessions), server,
            screenshots.Count(t => t >= from && t < to), mc, advancements, PlayStatsService.BadgeFor(hours));
    }

    /// <summary>Когда игрок чаще всего играет: время сессий раскладывается по часам суток.</summary>
    internal static string Rhythm(IEnumerable<PlaySession> sessions)
    {
        var byHour = new double[24];
        foreach (var s in sessions)
        {
            var t = s.Start.LocalDateTime;
            var end = (s.End ?? DateTimeOffset.Now).LocalDateTime;
            while (t < end)
            {
                var next = t.Date.AddHours(t.Hour + 1);
                var chunk = (end < next ? end : next) - t;
                byHour[t.Hour] += chunk.TotalMinutes;
                t = next;
            }
        }
        if (byHour.Sum() <= 0) return "";
        double Sum(int a, int b) => Enumerable.Range(a, b - a).Sum(h => byHour[h % 24]);
        var buckets = new[] { ("night", Sum(22, 29)), ("morning", Sum(5, 11)), ("day", Sum(11, 17)), ("evening", Sum(17, 22)) };
        return buckets.MaxBy(b => b.Item2).Item1;
    }

    public static (string Emoji, string Title, string Text) RhythmText(string rhythm) => rhythm switch
    {
        "night" => ("🦉", L.T("Ночная сова"), L.T("Больше всего вы играли с 22:00 до 05:00")),
        "morning" => ("🌅", L.T("Ранняя пташка"), L.T("Больше всего вы играли с 05:00 до 11:00")),
        "day" => ("☀️", L.T("Дневной игрок"), L.T("Больше всего вы играли с 11:00 до 17:00")),
        _ => ("🌆", L.T("Вечерний игрок"), L.T("Больше всего вы играли с 17:00 до 22:00")),
    };

    public static int CardCount(YearRecap r) => r.IsEmpty ? 1 : 8;

    // ---------- Рендер ----------

    private static readonly Color Ink = Colors.White;
    private static readonly Color Muted = Color.FromRgb(0xB6, 0xC2, 0xD2);

    /// <summary>Фон каждой карточки — свой, чтобы серия листалась как сторис.</summary>
    private static readonly (Color A, Color B)[] Backgrounds =
    {
        (Color.FromRgb(0x05, 0x2E, 0x16), Color.FromRgb(0x0B, 0x12, 0x20)),
        (Color.FromRgb(0x16, 0x65, 0x34), Color.FromRgb(0x05, 0x2E, 0x16)),
        (Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0x0B, 0x12, 0x20)),
        (Color.FromRgb(0x7C, 0x2D, 0x12), Color.FromRgb(0x1C, 0x19, 0x17)),
        (Color.FromRgb(0x4C, 0x1D, 0x95), Color.FromRgb(0x0B, 0x12, 0x20)),
        (Color.FromRgb(0x0F, 0x17, 0x2A), Color.FromRgb(0x31, 0x2E, 0x81)),
        (Color.FromRgb(0x13, 0x4E, 0x4A), Color.FromRgb(0x0B, 0x12, 0x20)),
        (Color.FromRgb(0x05, 0x2E, 0x16), Color.FromRgb(0x14, 0x53, 0x2D)),
    };

    public static BitmapSource Render(YearRecap r, int index, string nick, Color accent)
    {
        var (a, b) = Backgrounds[index % Backgrounds.Length];
        var root = new Grid { Width = Width, Height = Height, Background = new LinearGradientBrush(a, b, 70) };
        // Крупные мягкие круги на фоне.
        root.Children.Add(new Ellipse { Width = 900, Height = 900, Fill = new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -300, -350, 0) });
        root.Children.Add(new Ellipse { Width = 700, Height = 700, Fill = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-300, 0, 0, -250) });

        var header = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(80, 80, 80, 0) };
        var count = CardCount(r);
        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(dots, Dock.Right);
        for (var i = 0; i < count; i++)
            dots.Children.Add(new Border { Width = i == index ? 56 : 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(i == index ? accent : Color.FromArgb(90, 255, 255, 255)) });
        header.Children.Add(dots);
        header.Children.Add(Text(L.T($"🐸 Мой {r.Year} в Minecraft"), 40, FontWeights.Bold, Muted, 0, HorizontalAlignment.Left));
        root.Children.Add(header);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(80, 0, 80, 0) };
        root.Children.Add(body);
        var footer = Text(L.T("Ribbix · #МойГодВMinecraft"), 36, FontWeights.SemiBold, Color.FromRgb(0xA7, 0xF3, 0xD0), 0);
        footer.VerticalAlignment = VerticalAlignment.Bottom;
        footer.Margin = new Thickness(80, 0, 80, 90);
        root.Children.Add(footer);

        if (r.IsEmpty) index = -1;
        switch (index)
        {
            case -1:
                Big(body, "🌱", L.T($"{r.Year} только начинается"), accent);
                Line(body, L.T("Сыграйте из Ribbix — и здесь появятся ваши итоги года"), 52, Muted, 30);
                break;
            case 0:
                Logo(body);
                Line(body, nick, 110, Ink, 40, FontWeights.Black);
                Line(body, L.T($"Мой {r.Year} в Minecraft"), 72, accent, 20, FontWeights.Bold);
                Line(body, L.T("Вот как прошёл ваш год в пруду →"), 46, Muted, 60);
                break;
            case 1:
                Big(body, "⏱", Hours(r.Total), accent);
                Line(body, L.T("в игре за год"), 56, Ink, 10, FontWeights.SemiBold);
                Line(body, L.T($"{r.Sessions} {Plural(r.Sessions, "сессия", "сессии", "сессий")} · {r.DaysPlayed} {Plural(r.DaysPlayed, "день", "дня", "дней")} в игре"), 46, Muted, 50);
                Line(body, L.T($"Это {Math.Round(r.Total.TotalHours / 24, 1)} суток без перерыва"), 46, Muted, 16);
                break;
            case 2:
                Line(body, L.T("Любимая версия"), 52, Muted, 0, FontWeights.SemiBold);
                Big(body, "💚", r.Favorite?.Name ?? "—", accent, 96);
                if (r.Favorite is { } f)
                {
                    Line(body, L.T($"{Hours(f.Time)} · {Math.Round(f.Share * 100)}% всего времени"), 50, Ink, 30);
                    Line(body, L.T($"{f.Sessions} {Plural(f.Sessions, "сессия", "сессии", "сессий")} · всего версий за год: {r.VersionsPlayed}"), 44, Muted, 16);
                }
                if (r.FavoriteServer is { } s) Line(body, L.T($"Любимый сервер: {s}"), 44, Muted, 16);
                break;
            case 3:
                Line(body, L.T("Самый активный день"), 52, Muted, 0, FontWeights.SemiBold);
                Big(body, "🔥", r.BestDay?.ToString("d MMMM", L.Culture) ?? "—", accent);
                Line(body, L.T($"{Hours(r.BestDayTime)} за один день"), 50, Ink, 20);
                Line(body, L.T($"Самая долгая сессия — {Hours(r.Longest)}"), 44, Muted, 40);
                Line(body, L.T($"Лучшая серия — {r.BestStreak} {Plural(r.BestStreak, "день", "дня", "дней")} подряд"), 44, Muted, 16);
                break;
            case 4:
                Line(body, L.T("Год по месяцам"), 52, Muted, 0, FontWeights.SemiBold);
                Months(body, r, accent);
                Line(body, L.T($"Лучший месяц — {L.Culture.DateTimeFormat.GetMonthName(r.BestMonth + 1)}"), 56, Ink, 40, FontWeights.Bold);
                Line(body, Hours(r.Months[r.BestMonth]), 46, Muted, 10);
                break;
            case 5:
                var (emoji, title, text) = RhythmText(r.Rhythm);
                Line(body, L.T("Ваш ритм"), 52, Muted, 0, FontWeights.SemiBold);
                Big(body, emoji, title, accent);
                Line(body, text, 46, Ink, 20);
                if (r.Screenshots > 0) Line(body, L.T($"📸 {r.Screenshots} {Plural(r.Screenshots, "скриншот", "скриншота", "скриншотов")} за год"), 44, Muted, 50);
                break;
            case 6:
                Line(body, L.T("В мирах Minecraft"), 52, Muted, 0, FontWeights.SemiBold);
                var km = r.Mc.WalkCm / 100_000.0;
                Big(body, "🥾", L.T($"{km:N1} км"), accent);
                Line(body, km >= MarathonKm
                    ? L.T($"пешком — это {Math.Floor(km / MarathonKm):N0} {Plural((int)Math.Floor(km / MarathonKm), "марафон", "марафона", "марафонов")}")
                    : L.T("пешком"), 46, Ink, 10);
                Grid4(body, accent,
                    (r.Mc.Jumps.ToString("N0"), L.T("прыжков")), (r.Mc.MobKills.ToString("N0"), L.T("мобов побеждено")),
                    (r.Mc.Deaths.ToString("N0"), L.T("смертей")), (r.Advancements.ToString("N0"), L.T("достижений")));
                Line(body, L.T("Счётчики миров — за всё время"), 34, Muted, 30);
                break;
            default:
                Line(body, nick, 80, Ink, 0, FontWeights.Black);
                Big(body, r.Badge.Emoji, r.Badge.Title, accent, 110);
                Line(body, L.T($"Мой {r.Year} в Minecraft"), 50, Muted, 10);
                Grid4(body, accent,
                    (Hours(r.Total), L.T("в игре")), (r.Favorite?.Name ?? "—", L.T("любимая версия")),
                    (r.BestDay?.ToString("d MMM", L.Culture) ?? "—", L.T("самый активный день")),
                    (L.T($"{r.Mc.WalkCm / 100_000.0:N0} км"), L.T("пешком")));
                break;
        }

        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    public static string Save(BitmapSource card, string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(card));
        using var fs = File.Create(file);
        enc.Save(fs);
        return file;
    }

    public static string OutputDir(int year) => System.IO.Path.Combine(ShareCardService.OutputDir, L.T($"Мой год {year}"));

    private static string Hours(TimeSpan t) => ViewModels.StatsViewModel.Hours(t);
    private static string Plural(int n, string one, string few, string many) => ViewModels.MainViewModel.Plural(n, one, few, many);

    private static void Logo(Panel body)
    {
        try
        {
            body.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/RibbixLauncher;component/Assets/main/logo.png")),
                Height = 280, HorizontalAlignment = HorizontalAlignment.Center });
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException) { }
    }

    private static void Big(Panel body, string emoji, string value, Color accent, double size = 130)
    {
        body.Children.Add(Text(emoji, 150, FontWeights.Normal, Ink, 30, font: "Segoe UI Emoji"));
        var t = Text(value, size, FontWeights.Black, Ink, 20);
        t.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 50, ShadowDepth = 0, Opacity = 0.8 };
        body.Children.Add(t);
    }

    private static void Line(Panel body, string text, double size, Color color, double top, FontWeight? weight = null) =>
        body.Children.Add(Text(text, size, weight ?? FontWeights.Normal, color, top));

    private static void Months(Panel body, YearRecap r, Color accent)
    {
        var max = Math.Max(1, r.Months.Max(m => m.TotalMinutes));
        var grid = new UniformGrid { Rows = 1, Height = 560, Margin = new Thickness(0, 50, 0, 0) };
        for (var m = 0; m < 12; m++)
        {
            var col = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
            var name = L.Culture.DateTimeFormat.GetAbbreviatedMonthName(m + 1).TrimEnd('.');
            var label = Text(name.Length > 3 ? name[..3] : name, 30, FontWeights.SemiBold, m == r.BestMonth ? Ink : Muted, 12);
            label.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(label, Dock.Bottom);
            col.Children.Add(label);
            col.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(12, 12, 4, 4),
                Height = r.Months[m] > TimeSpan.Zero ? Math.Max(10, r.Months[m].TotalMinutes / max * 480) : 4,
                Background = new SolidColorBrush(m == r.BestMonth ? accent : Color.FromArgb(120, 255, 255, 255)),
            });
            grid.Children.Add(col);
        }
        body.Children.Add(grid);
    }

    private static void Grid4(Panel body, Color accent, params (string Value, string Label)[] cells)
    {
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 60, 0, 0) };
        foreach (var (v, l) in cells)
        {
            var card = new Border { CornerRadius = new CornerRadius(32), Margin = new Thickness(12), Padding = new Thickness(24, 30, 24, 30),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(3) };
            var s = new StackPanel();
            var value = Text(v, v.Length > 9 ? 50 : 64, FontWeights.Black, Ink, 0);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.TextWrapping = TextWrapping.NoWrap;
            s.Children.Add(value);
            s.Children.Add(Text(l, 34, FontWeights.Normal, Muted, 8));
            card.Child = s;
            grid.Children.Add(card);
        }
        body.Children.Add(grid);
    }

    private static TextBlock Text(string text, double size, FontWeight weight, Color color, double top,
        HorizontalAlignment align = HorizontalAlignment.Center, string font = "Segoe UI") => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(color),
        FontFamily = new FontFamily(font), HorizontalAlignment = align,
        TextAlignment = align == HorizontalAlignment.Left ? TextAlignment.Left : TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };
}
