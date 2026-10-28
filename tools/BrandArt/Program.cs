// BrandArt: пиксельные надписи «RIBBIX LAUNCHER» поверх ассетов. Лягушка — из старого логотипа, текст рисуется
// мелким растровым шрифтом без сглаживания и увеличивается «по пикселям», как в остальной графике лаунчера.
//   BrandArt logo <лого1024.png> <выход1024.png> <выход-маленький.png> <ширина> <высота>
//   BrandArt title <png> <выход.png> x0 y0 x1 y1 [высота_текста]   — заменить надпись в прямоугольнике
//   BrandArt overlay <фон.png> <лого.png> <выход.png> x y w h       — положить логотип поверх фона
//   BrandArt bbox <png> x0 y0 x1 y1                                  — границы яркого текста в прямоугольнике
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    [STAThread]
    static int Main(string[] a)
    {
        int I(int i) => int.Parse(a[i], CultureInfo.InvariantCulture);
        switch (a[0])
        {
            case "logo": Logo(a[1], a[2], a[3], I(4), I(5)); break;
            case "title": Title(a[1], a[2], I(3), I(4), I(5), I(6), a.Length > 7 ? I(7) : 0); break;
            case "overlay": Overlay(a[1], a[2], a[3], I(4), I(5), I(6), I(7)); break;
            case "bbox":
                var img = Img.Load(a[1]);
                Console.WriteLine(img.TextBox(I(2), I(3), I(4), I(5)));
                break;
        }
        return 0;
    }

    static readonly Color Lime1 = C(0xE4, 0xFF, 0x5A), Lime2 = C(0x5C, 0xD1, 0x1E);
    static readonly Color Cyan1 = C(0xB8, 0xFB, 0xFF), Cyan2 = C(0x1F, 0xB6, 0xE8);
    static readonly Color Ink = C(0x00, 0x04, 0x14);

    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Логотип: верх старого (лягушка) + новая плашка с «RIBBIX» и «LAUNCHER».</summary>
    static void Logo(string src, string out1024, string outSmall, int smallW, int smallH)
    {
        var s = Img.Load(src);
        const int cut = 468, textTop = 478;
        var ribbix = Pixel.Bevel(Pixel.Glyphs("RIBBIX", Font.Bold, Lime1, Lime2, Ink, 1).Scale(20), Ink, 8);
        var launcher = Pixel.Bevel(Pixel.Glyphs("LAUNCHER", Font.Bold, Cyan1, Cyan2, Ink, 1).Scale(11), Ink, 5);
        var bottom = textTop + ribbix.H + 6 + launcher.H + 24;
        var o = new Img(s.W, s.H);
        // Плашка как у старого логотипа: тёмно-синяя с неоновой рамкой фиолетовый → голубой.
        o.RoundRect(78, 420, 946, bottom + 22, 64, C(0x6B, 0x47, 0xFD), C(0x22, 0xD3, 0xEE));
        o.RoundRect(94, 430, 930, bottom + 6, 52, C(0x00, 0x0A, 0x21), C(0x03, 0x04, 0x32));
        // Лягушка и верх старой плашки — как были.
        for (var y = 0; y < cut; y++) for (var x = 0; x < s.W; x++) o.Blend(x, y, s.Get(x, y));
        o.Draw(ribbix, (o.W - ribbix.W) / 2, textTop);
        o.Draw(launcher, (o.W - launcher.W) / 2, textTop + ribbix.H + 6);
        o.Save(out1024);
        var trimmed = o.Trim();
        trimmed.Save(Path.ChangeExtension(out1024, ".trim.png"));
        trimmed.Resize(smallW, smallH).Save(outSmall);
        Console.WriteLine($"logo: ribbix {ribbix.W}x{ribbix.H}, launcher {launcher.W}x{launcher.H}");
    }

    /// <summary>Надпись «FrogLauncher» в рамке/плашке → «RibbixLauncher» тем же стилем (зелёный + голубой).</summary>
    static void Title(string src, string dst, int x0, int y0, int x1, int y1, int textH)
    {
        var img = Img.Load(src);
        // textH < 0 — прямоугольник задан точно (рядом с надписью есть яркая рамка, автопоиск её цепляет).
        var box = textH < 0 ? new Box(x0, y0, x1 - x0, y1 - y0) : img.TextBox(x0, y0, x1, y1);
        if (textH < 0) textH = 0;
        if (box.W <= 0) throw new InvalidOperationException("надпись не найдена");
        var bg = img.Get(box.X - 3, box.Y + box.H / 2);
        img.Fill(box.X - 1, box.Y - 1, box.W + 3, box.H + 3, bg);
        var h = textH > 0 ? textH : box.H;
        // Самое крупное целое увеличение тонкого пиксельного шрифта, которое влезает в прямоугольник надписи.
        var a0 = Pixel.Glyphs("Ribbix", Font.Thin, C(0x9B, 0xFF, 0x4A), C(0x4A, 0xD1, 0x1A), Color.FromArgb(0, 0, 0, 0), 0);
        var b0 = Pixel.Glyphs("Launcher", Font.Thin, C(0xA8, 0xF5, 0xFF), C(0x38, 0xBD, 0xF8), Color.FromArgb(0, 0, 0, 0), 0);
        var scale = 1;
        while ((a0.W + b0.W + 1) * (scale + 1) <= box.W && a0.H * (scale + 1) <= h + 4) scale++;
        var a = a0.Scale(scale);
        var b = b0.Scale(scale);
        var y = box.Y + (box.H - a.H) / 2;
        img.Draw(a, box.X, y);
        img.Draw(b, box.X + a.W, y);
        img.Save(dst);
        Console.WriteLine($"title {Path.GetFileName(src)}: box {box}, scale {scale}, width {a.W + b.W + scale}");
    }

    static void Overlay(string bgPath, string logoPath, string dst, int x, int y, int w, int h)
    {
        var bg = Img.Load(bgPath);
        var logo = Img.Load(logoPath).Resize(w, h);
        bg.Draw(logo, x, y);
        bg.Save(dst);
    }
}

readonly record struct Box(int X, int Y, int W, int H);

/// <summary>BGRA-картинка с простыми операциями.</summary>
sealed class Img(int w, int h)
{
    public readonly int W = w, H = h;
    public readonly byte[] Px = new byte[w * h * 4];

    public static Img Load(string path)
    {
        var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var f = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        var img = new Img(f.PixelWidth, f.PixelHeight);
        f.CopyPixels(img.Px, img.W * 4, 0);
        return img;
    }

    public void Save(string path)
    {
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, Px, W * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public Color Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return Colors.Transparent;
        var o = (y * W + x) * 4;
        return Color.FromArgb(Px[o + 3], Px[o + 2], Px[o + 1], Px[o]);
    }

    public void Set(int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        var o = (y * W + x) * 4;
        Px[o] = c.B; Px[o + 1] = c.G; Px[o + 2] = c.R; Px[o + 3] = c.A;
    }

    public void Blend(int x, int y, Color c)
    {
        if (c.A == 0) return;
        if (c.A == 255) { Set(x, y, c); return; }
        var d = Get(x, y);
        double a = c.A / 255.0, da = d.A / 255.0, oa = a + da * (1 - a);
        byte Mix(byte s, byte t) => (byte)Math.Round((s * a + t * da * (1 - a)) / Math.Max(oa, 1e-6));
        Set(x, y, Color.FromArgb((byte)Math.Round(oa * 255), Mix(c.R, d.R), Mix(c.G, d.G), Mix(c.B, d.B)));
    }

    public void Fill(int x, int y, int w, int h, Color c)
    {
        for (var j = y; j < y + h; j++) for (var i = x; i < x + w; i++) Set(i, j, c);
    }

    public void Draw(Img src, int x, int y)
    {
        for (var j = 0; j < src.H; j++) for (var i = 0; i < src.W; i++) Blend(x + i, y + j, src.Get(i, j));
    }

    /// <summary>Скруглённый прямоугольник с вертикальным градиентом (ступенчатые углы — как пиксель-арт).</summary>
    public void RoundRect(int x0, int y0, int x1, int y1, int r, Color top, Color bottom)
    {
        for (var y = y0; y < y1; y++)
        {
            var t = (y - y0) / (double)Math.Max(1, y1 - y0);
            var c = Color.FromRgb((byte)(top.R + (bottom.R - top.R) * t), (byte)(top.G + (bottom.G - top.G) * t), (byte)(top.B + (bottom.B - top.B) * t));
            for (var x = x0; x < x1; x++)
            {
                int dx = Math.Max(0, Math.Max(x0 + r - x, x - (x1 - r))), dy = Math.Max(0, Math.Max(y0 + r - y, y - (y1 - r)));
                // Квантуем по 8 пикселей — углы «лесенкой».
                dx = dx / 8 * 8; dy = dy / 8 * 8;
                if (dx * dx + dy * dy <= r * r) Set(x, y, c);
            }
        }
    }

    /// <summary>Границы ярких насыщенных пикселей (текст) в прямоугольнике.</summary>
    public Box TextBox(int x0, int y0, int x1, int y1)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var c = Get(x, y);
            var max = Math.Max(c.R, Math.Max(c.G, c.B));
            var min = Math.Min(c.R, Math.Min(c.G, c.B));
            if (c.A < 200 || max < 150 || max - min < 60) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? new Box(0, 0, 0, 0) : new Box(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public Img Scale(int k)
    {
        var o = new Img(W * k, H * k);
        for (var y = 0; y < o.H; y++) for (var x = 0; x < o.W; x++) o.Set(x, y, Get(x / k, y / k));
        return o;
    }

    public Img Trim()
    {
        int minX = W, minY = H, maxX = -1, maxY = -1;
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++)
            if (Get(x, y).A > 8) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        var o = new Img(maxX - minX + 1, maxY - minY + 1);
        for (var y = 0; y < o.H; y++) for (var x = 0; x < o.W; x++) o.Set(x, y, Get(minX + x, minY + y));
        return o;
    }

    public Img Resize(int w, int h)
    {
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, Px, W * 4);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(bmp, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var f = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var o = new Img(w, h);
        f.CopyPixels(o.Px, w * 4, 0);
        return o;
    }
}

enum Font { Bold, Thin }

/// <summary>Пиксельные шрифты по клеткам — только буквы для «RIBBIX LAUNCHER» / «Ribbix Launcher».</summary>
static class Glyphs
{
    public static readonly Dictionary<char, string[]> Bold = new()
    {
        ['R'] = new[] { "#####.", "##..##", "##..##", "#####.", "####..", "##.##.", "##..##", "##..##" },
        ['I'] = new[] { "####", ".##.", ".##.", ".##.", ".##.", ".##.", ".##.", "####" },
        ['B'] = new[] { "#####.", "##..##", "##..##", "#####.", "##..##", "##..##", "##..##", "#####." },
        ['X'] = new[] { "##..##", "##..##", ".####.", "..##..", "..##..", ".####.", "##..##", "##..##" },
        ['L'] = new[] { "##....", "##....", "##....", "##....", "##....", "##....", "##....", "######" },
        ['A'] = new[] { "..##..", ".####.", "##..##", "##..##", "######", "##..##", "##..##", "##..##" },
        ['U'] = new[] { "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", "##..##", ".####." },
        ['N'] = new[] { "##..##", "###.##", "######", "##.###", "##..##", "##..##", "##..##", "##..##" },
        ['C'] = new[] { ".####.", "##..##", "##....", "##....", "##....", "##....", "##..##", ".####." },
        ['H'] = new[] { "##..##", "##..##", "##..##", "######", "##..##", "##..##", "##..##", "##..##" },
        ['E'] = new[] { "######", "##....", "##....", "#####.", "##....", "##....", "##....", "######" },
    };

    public static readonly Dictionary<char, string[]> Thin = new()
    {
        ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
        ['i'] = new[] { "#", ".", "#", "#", "#", "#", "#" },
        ['b'] = new[] { "#....", "#....", "####.", "#...#", "#...#", "#...#", "####." },
        ['x'] = new[] { ".....", ".....", "#...#", ".#.#.", "..#..", ".#.#.", "#...#" },
        ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
        ['a'] = new[] { ".....", ".....", ".###.", "....#", ".####", "#...#", ".####" },
        ['u'] = new[] { ".....", ".....", "#...#", "#...#", "#...#", "#...#", ".####" },
        ['n'] = new[] { ".....", ".....", "####.", "#...#", "#...#", "#...#", "#...#" },
        ['c'] = new[] { "....", "....", ".###", "#...", "#...", "#...", ".###" },
        ['h'] = new[] { "#....", "#....", "####.", "#...#", "#...#", "#...#", "#...#" },
        ['e'] = new[] { ".....", ".....", ".###.", "#...#", "#####", "#....", ".###." },
        ['r'] = new[] { "....", "....", "#.##", "##..", "#...", "#...", "#..." },
    };
}

/// <summary>Растровый текст без сглаживания: маска → градиент сверху вниз, блик, тень и контур.</summary>
static class Pixel
{
    /// <summary>Текст из клеточного шрифта: та же отрисовка (градиент, блик, тень, контур), что и у Text.</summary>
    public static Img Glyphs(string text, Font font, Color top, Color bottom, Color outline, int outlineW)
    {
        var map = font == Font.Bold ? global::Glyphs.Bold : global::Glyphs.Thin;
        var rows = map.Values.First().Length;
        var width = text.Sum(ch => map[ch][0].Length) + text.Length - 1;
        var mask = new bool[width, rows];
        var x0 = 0;
        foreach (var ch in text)
        {
            var g = map[ch];
            for (var y = 0; y < rows; y++) for (var x = 0; x < g[y].Length; x++) mask[x0 + x, y] = g[y][x] == '#';
            x0 += g[0].Length + 1;
        }
        return Render(mask, width, rows, top, bottom, outline, outlineW, bevel: false);
    }

    public static Img Text(string text, double size, Color top, Color bottom, Color outline, int outlineW)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Arial Black"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), size, Brushes.White, 1.0);
        int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2, h = (int)Math.Ceiling(ft.Height) + 2;
        var dv = new DrawingVisual();
        TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(dv, EdgeMode.Aliased);
        using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(1, 1));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        var mask = new bool[w, h];
        int minY = h, maxY = 0, minX = w, maxX = 0;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            if (px[(y * w + x) * 4 + 3] > 110)
            {
                mask[x, y] = true;
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            }

        var cropped = new bool[maxX - minX + 1, maxY - minY + 1];
        for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++) cropped[x - minX, y - minY] = mask[x, y];
        return Render(cropped, maxX - minX + 1, maxY - minY + 1, top, bottom, outline, outlineW);
    }

    static Img Render(bool[,] mask, int w, int h, Color top, Color bottom, Color outline, int outlineW, bool bevel = true)
    {
        const int minX = 0, minY = 0;
        int maxX = w - 1, maxY = h - 1;
        var pad = outline.A == 0 ? 0 : outlineW;
        var o = new Img(w + pad * 2, h + pad * 2 + (pad > 0 ? 1 : 0));
        bool M(int x, int y) { x += minX - pad; y += minY - pad; return x >= 0 && y >= 0 && x < w && y < h && mask[x, y]; }
        for (var y = 0; y < o.H; y++)
        for (var x = 0; x < o.W; x++)
        {
            if (M(x, y))
            {
                var t = (y - pad) / (double)Math.Max(1, maxY - minY);
                var c = Lerp(top, bottom, t);
                if (bevel && !M(x, y - 1)) c = Lerp(c, Colors.White, 0.45);          // блик по верхнему краю
                else if (bevel && !M(x, y + 1)) c = Lerp(c, Colors.Black, 0.25);     // тень снизу
                o.Set(x, y, c);
                continue;
            }
            if (outline.A == 0 || outlineW == 0) continue;
            var near = false;
            for (var dy = -outlineW; dy <= outlineW + 1 && !near; dy++)
                for (var dx = -outlineW; dx <= outlineW && !near; dx++)
                    near = M(x + dx, y + dy);
            if (near) o.Set(x, y, outline);
        }
        return o;
    }

    /// <summary>Фаска на уже увеличенном тексте: светлая полоса d пикселей сверху у каждой грани, тёмная — снизу.</summary>
    public static Img Bevel(Img img, Color ink, int d)
    {
        bool Text(int x, int y)
        {
            var c = img.Get(x, y);
            return c.A > 0 && !(c.R == ink.R && c.G == ink.G && c.B == ink.B);
        }
        var o = new Img(img.W, img.H);
        Array.Copy(img.Px, o.Px, img.Px.Length);
        for (var y = 0; y < img.H; y++)
        for (var x = 0; x < img.W; x++)
        {
            if (!Text(x, y)) continue;
            var c = img.Get(x, y);
            if (!Text(x, y - d)) o.Set(x, y, Lerp(c, Colors.White, 0.5));
            else if (!Text(x, y + d)) o.Set(x, y, Lerp(c, Colors.Black, 0.3));
        }
        return o;
    }

    static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}
