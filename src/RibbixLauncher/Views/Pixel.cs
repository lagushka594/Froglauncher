using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RibbixLauncher.Views;

/// <summary>
/// Рамка в стиле логотипа Ribbix: углы «лесенкой» (два шага по Step), неоновая обводка BorderBrush,
/// светлая фаска сверху и тёмная снизу (Bevel), «подставка» под кнопкой (Depth — тень ниже на столько пикселей).
/// Наследник Border: раскладка (BorderThickness, Padding, Child) — как у обычного Border, иначе только рисование.
/// </summary>
public class PixelBorder : Border
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(PixelBorder),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(nameof(Depth), typeof(double), typeof(PixelBorder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DepthBrushProperty = DependencyProperty.Register(nameof(DepthBrush), typeof(Brush), typeof(PixelBorder),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0xCC, 0x02, 0x05, 0x12)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BevelProperty = DependencyProperty.Register(nameof(Bevel), typeof(bool), typeof(PixelBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Размер «пикселя» углов.</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    /// <summary>Высота «подставки»: фигура рисуется выше на Depth, под ней — тень. Нажатая кнопка — Depth = 0.</summary>
    public double Depth { get => (double)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }
    public Brush DepthBrush { get => (Brush)GetValue(DepthBrushProperty); set => SetValue(DepthBrushProperty, value); }
    public bool Bevel { get => (bool)GetValue(BevelProperty); set => SetValue(BevelProperty, value); }

    private static readonly Brush Light = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Dark = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - Depth, t = BorderThickness.Left;
        // На тонких полосках (прогресс) ступенька сама уменьшается.
        var s = Math.Min(Math.Max(1, Step), Math.Floor(Math.Min(w, h) / 4));
        if (s < 1) return;
        if (Depth > 0) dc.DrawGeometry(DepthBrush, null, Stepped(0, Depth, w, h, s));
        var inner = t > 0 ? Stepped(t, t, w - 2 * t, h - 2 * t, s) : Stepped(0, 0, w, h, s);
        // Рамка — кольцом (внешняя фигура минус внутренняя), чтобы полупрозрачный фон не просвечивал её заливку.
        if (BorderBrush is { } border && t > 0)
            dc.DrawGeometry(border, null, new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { Stepped(0, 0, w, h, s), inner } });
        if (Background is { } bg) dc.DrawGeometry(bg, null, inner);
        if (!Bevel || Background is null || h - 2 * t < 4 * s) return;
        // Фаска: светлая полоса под верхней кромкой, тёмная над нижней.
        var band = Math.Max(2, Math.Round(s / 2));
        dc.DrawRectangle(Light, null, new Rect(t + 2 * s, t, Math.Max(0, w - 2 * t - 4 * s), band));
        dc.DrawRectangle(Dark, null, new Rect(t + 2 * s, h - t - band, Math.Max(0, w - 2 * t - 4 * s), band));
    }

    /// <summary>Прямоугольник с углами в два шага: как скругление, но пикселями.</summary>
    internal static Geometry Stepped(double x, double y, double w, double h, double s)
    {
        double r = x + w, b = y + h;
        var pts = new[]
        {
            new Point(x + 2 * s, y), new Point(r - 2 * s, y), new Point(r - 2 * s, y + s), new Point(r - s, y + s),
            new Point(r - s, y + 2 * s), new Point(r, y + 2 * s), new Point(r, b - 2 * s), new Point(r - s, b - 2 * s),
            new Point(r - s, b - s), new Point(r - 2 * s, b - s), new Point(r - 2 * s, b), new Point(x + 2 * s, b),
            new Point(x + 2 * s, b - s), new Point(x + s, b - s), new Point(x + s, b - 2 * s), new Point(x, b - 2 * s),
            new Point(x, y + 2 * s), new Point(x + s, y + 2 * s), new Point(x + s, y + s), new Point(x + 2 * s, y + s),
        };
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts[1..], false, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Пиксельная иконка по клеткам (12×12), цвет — Foreground. Чёткая при любом размере.</summary>
public class PixelIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(PixelIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(PixelIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(PixelIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Цвет клеток «+» (вторая краска иконки); по умолчанию — Foreground, светлее.</summary>
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public PixelIcon()
    {
        Width = 24;
        Height = 24;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Icons.TryGetValue(Kind ?? "", out var rows)) return;
        var cell = Math.Min(ActualWidth / rows[0].Length, ActualHeight / rows.Length);
        var ox = (ActualWidth - cell * rows[0].Length) / 2;
        var oy = (ActualHeight - cell * rows.Length) / 2;
        var accent = Accent ?? new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        var shadow = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00));
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
            if (rows[y][x] is '#' or '+')
                dc.DrawRectangle(shadow, null, new Rect(ox + x * cell + cell * 0.25, oy + y * cell + cell * 0.35, cell, cell));
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
        {
            var brush = rows[y][x] switch { '#' => Foreground, '+' => accent, _ => null };
            if (brush is not null) dc.DrawRectangle(brush, null, new Rect(ox + x * cell, oy + y * cell, cell + 0.4, cell + 0.4));
        }
    }

    /// <summary>12×12: «#» — основной цвет, «+» — блик/вторая краска.</summary>
    public static readonly Dictionary<string, string[]> Icons = new()
    {
        ["home"] = new[]
        {
            ".....##.....", "....####....", "...######...", "..########..", ".##########.", "############",
            ".##########.", ".###.++.###.", ".###.++.###.", ".###.++.###.", ".###.++.###.", ".##########.",
        },
        ["catalog"] = new[]
        {
            "#####..#####", "#+++#..#+++#", "#+++#..#+++#", "#####..#####", "............", "............",
            "#####..#####", "#+++#..#+++#", "#+++#..#+++#", "#####..#####", "............", "............",
        },
        ["modpack"] = new[]
        {
            ".....##.....", "...##++##...", ".##++++++##.", "#++++++++++#", "#####++#####", "#####..#####",
            "######.#####", "######.#####", "######.#####", ".#####.####.", "...###.##...", ".....##.....",
        },
        ["servers"] = new[]
        {
            "############", "#++########+", "############", "............", "############", "#++########+",
            "############", "............", "############", "#++########+", "############", "............",
        },
        ["java"] = new[]
        {
            "...+..+.....", "..+..+......", "...+..+.....", "............", "#########...", "#########.##",
            "#########..#", "#########.##", ".#######....", "..#####.....", "............", "###########.",
        },
        ["crown"] = new[]
        {
            "............", "+....+....+.", "#...###...#.", "##.#####.##.", "###########.", "###########.",
            "##+##+##+##.", "###########.", "###########.", "............", "............", "............",
        },
        ["profile"] = new[]
        {
            "....####....", "...######...", "...#+##+#...", "...######...", "...######...", "....####....",
            "............", "..########..", ".##########.", "############", "############", "############",
        },
        ["settings"] = new[]
        {
            ".....##.....", "..#.####.#..", ".##########.", "..###..###..", ".###....###.", "####....####",
            "####....####", ".###....###.", "..###..###..", ".##########.", "..#.####.#..", ".....##.....",
        },
        ["help"] = new[]
        {
            "...######...", "..########..", ".###....###.", ".###....###.", "........###.", ".......###..",
            ".....####...", ".....###....", ".....###....", "............", ".....###....", ".....###....",
        },
        ["exit"] = new[]
        {
            "#######.....", "#+++++#.....", "#+++++#..#..", "#+++++#..##.", "#+++++######", "#++++#######",
            "#+++++######", "#+++++#..##.", "#+++++#..#..", "#+++++#.....", "#######.....", "............",
        },
        ["play"] = new[]
        {
            "##..........", "####........", "######......", "########....", "##########..", "############",
            "############", "##########..", "########....", "######......", "####........", "##..........",
        },
        ["folder"] = new[]
        {
            "............", "#####.......", "#+++##......", "############", "#++++++++++#", "#++++++++++#",
            "#++++++++++#", "#++++++++++#", "#++++++++++#", "############", "............", "............",
        },
        ["back"] = new[]
        {
            "............", ".....#......", "....##......", "...#########", "..##########", ".###########",
            "..##########", "...#########", "....##......", ".....#......", "............", "............",
        },
        ["min"] = new[]
        {
            "............", "............", "............", "............", "............", "............",
            "............", "............", "............", "############", "############", "............",
        },
        ["max"] = new[]
        {
            "############", "############", "#..........#", "#..........#", "#..........#", "#..........#",
            "#..........#", "#..........#", "#..........#", "#..........#", "############", "............",
        },
        ["close"] = new[]
        {
            "##........##", "###......###", ".###....###.", "..###..###..", "...######...", "....####....",
            "....####....", "...######...", "..###..###..", ".###....###.", "###......###", "##........##",
        },
        ["warning"] = new[]
        {
            ".....##.....", "....####....", "....####....", "...##++##...", "...##++##...", "..###++###..",
            "..###++###..", ".####++####.", ".##########.", "#####++#####", "#####++#####", "############",
        },
        ["info"] = new[]
        {
            "...######...", ".##########.", ".####++####.", "############", "#####++#####", "############",
            "#####++#####", "#####++#####", "#####++#####", ".####++####.", ".##########.", "...######...",
        },
        ["error"] = new[]
        {
            "...######...", ".##########.", ".##+####+##.", "###++##++###", "####++++####", "#####++#####",
            "####++++####", "###++##++###", ".##+####+##.", ".##########.", ".##########.", "...######...",
        },
        ["mods"] = new[]
        {
            "....####....", "..##++++##..", "##++++++++##", "##++++++++##", "####++++####", "######++####",
            "############", "############", "############", "############", "..########..", "....####....",
        },
        ["dot"] = new[]
        {
            "............", "............", "............", "....####....", "...######...", "...######...",
            "...######...", "...######...", "....####....", "............", "............", "............",
        },
        ["chat"] = new[]
        {
            "############", "#++++++++++#", "#+##+##+##+#", "#+##+##+##+#", "#++++++++++#", "#++++++++++#",
            "############", "...####.....", "...###......", "...##.......", "............", "............",
        },
        ["note"] = new[]
        {
            "......####..", "......#####.", "......##.###", "......##..##", "......##....", "......##....",
            "......##....", "..######....", ".#######....", "########....", ".######.....", "..####......",
        },
        ["video"] = new[]
        {
            "............", ".##########.", "############", "####+#######", "####++######", "####+++#####",
            "####++######", "####+#######", "############", ".##########.", "............", "............",
        },
        ["code"] = new[]
        {
            "............", "...#....#...", "..##....##..", ".##..#...##.", "##...#....##", "##..#.....##",
            "##..#.....##", "##.#......##", ".##.#...##..", "..##....##..", "...#....#...", "............",
        },
        ["skin"] = new[]
        {
            "..##....##..", ".####..####.", "############", "############", "##.######.##", "...######...",
            "...######...", "...#++++#...", "...######...", "...######...", "...######...", "............",
        },
        ["stats"] = new[]
        {
            "..........##", "..........##", "......##..##", "......##..##", "......##..##", "..##..##..##",
            "..##..##..##", "..##..##..##", "..##..##..##", "..##..##..##", "............", "############",
        },
    };
}
