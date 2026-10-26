// Нарезка атласов ассетов FrogLauncher на отдельные PNG.
// Использование: AssetCutter <assets.map.json> <папка_атласов> <папка_вывода> [--sheet]
// Режимы фона: none — оставить как есть, dark — убрать тёмную подложку,
// checker — убрать нарисованную «шахматку» прозрачности.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length < 3)
{
    Console.WriteLine("AssetCutter <map.json> <atlasDir> <outDir> [--sheet]");
    return 1;
}

var map = JsonSerializer.Deserialize<AssetMap>(File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Пустая карта ассетов");
var atlasDir = args[1];
var outDir = args[2];
var makeSheet = args.Contains("--sheet");
var produced = new List<string>();

foreach (var atlas in map.Atlases)
{
    using var src = new Bitmap(Path.Combine(atlasDir, atlas.File));
    var dir = Path.Combine(outDir, atlas.Folder);
    Directory.CreateDirectory(dir);

    foreach (var a in atlas.Assets)
    {
        var rect = Rectangle.FromLTRB(a.Box[0], a.Box[1], a.Box[2], a.Box[3]);
        using var crop = src.Clone(rect, PixelFormat.Format32bppArgb);
        var mode = a.Bg ?? atlas.Bg;
        if (mode != "none") RemoveBackground(crop, mode, a.Tol);
        using var trimmed = mode == "none" ? new Bitmap(crop) : Trim(crop, 2);
        var path = Path.Combine(dir, a.Name + ".png");
        trimmed.Save(path, ImageFormat.Png);
        produced.Add(path);
        if (a.Ico == true) SaveIco(trimmed, Path.Combine(dir, a.Name + ".ico"));
        Console.WriteLine($"{atlas.Folder}/{a.Name}.png  {trimmed.Width}x{trimmed.Height}");
    }
}

if (makeSheet) SaveContactSheet(produced, Path.Combine(outDir, "_contact_sheet.png"));
return 0;

// Заливка от краёв кадра: всё, что связно с краем и похоже на фон, становится прозрачным.
static void RemoveBackground(Bitmap bmp, string mode, int? tol)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new int[w * h];
    Marshal.Copy(data.Scan0, px, 0, px.Length);

    int t = tol ?? (mode == "dark" ? 48 : 22);
    bool IsBg(int argb)
    {
        int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return mode switch
        {
            "dark" => max < t,
            "checker" => min > 255 - t * 3 && max - min < t,
            _ => false,
        };
    }

    var bg = new bool[w * h];
    var queue = new Queue<int>();
    void Seed(int x, int y)
    {
        int i = y * w + x;
        if (!bg[i] && IsBg(px[i])) { bg[i] = true; queue.Enqueue(i); }
    }
    for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
    for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

    while (queue.Count > 0)
    {
        int i = queue.Dequeue(), x = i % w, y = i / w;
        if (x > 0) Seed(x - 1, y);
        if (x < w - 1) Seed(x + 1, y);
        if (y > 0) Seed(x, y - 1);
        if (y < h - 1) Seed(x, y + 1);
    }

    for (int i = 0; i < px.Length; i++)
    {
        if (bg[i]) { px[i] = 0; continue; }
        // Полупрозрачная кайма на границе с фоном, чтобы не было «лесенки».
        int x = i % w, y = i / w, n = 0;
        if (x > 0 && bg[i - 1]) n++;
        if (x < w - 1 && bg[i + 1]) n++;
        if (y > 0 && bg[i - w]) n++;
        if (y < h - 1 && bg[i + w]) n++;
        if (n >= 2) px[i] = (px[i] & 0x00FFFFFF) | (0x99 << 24);
    }

    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
}

// Обрезка прозрачных полей вокруг изображения.
static Bitmap Trim(Bitmap bmp, int pad)
{
    int w = bmp.Width, h = bmp.Height, minX = w, minY = h, maxX = -1, maxY = -1;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new int[w * h];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    bmp.UnlockBits(data);

    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (((px[y * w + x] >> 24) & 255) > 16)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }

    if (maxX < 0) return new Bitmap(bmp);
    var r = Rectangle.FromLTRB(Math.Max(0, minX - pad), Math.Max(0, minY - pad),
        Math.Min(w, maxX + 1 + pad), Math.Min(h, maxY + 1 + pad));
    return bmp.Clone(r, PixelFormat.Format32bppArgb);
}

// ICO с PNG-кадрами 256/64/48/32/16 (формат Vista+), квадрат с сохранением пропорций.
static void SaveIco(Bitmap src, string path)
{
    int[] sizes = { 256, 64, 48, 32, 16 };
    var frames = new List<byte[]>();
    foreach (var s in sizes)
    {
        using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float k = Math.Min((float)s / src.Width, (float)s / src.Height);
            float w = src.Width * k, h = src.Height * k;
            g.DrawImage(src, (s - w) / 2, (s - h) / 2, w, h);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        frames.Add(ms.ToArray());
    }

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(frames[i].Length); bw.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var f in frames) bw.Write(f);
}

// Обзорный лист для визуальной проверки нарезки.
static void SaveContactSheet(List<string> files, string path)
{
    const int cell = 160, cols = 10;
    int rows = (files.Count + cols - 1) / cols;
    using var sheet = new Bitmap(cols * cell, rows * (cell + 14));
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(255, 200, 40, 200));
    using var font = new Font("Segoe UI", 7);
    for (int i = 0; i < files.Count; i++)
    {
        using var img = Image.FromFile(files[i]);
        float s = Math.Min(1f, Math.Min((cell - 8f) / img.Width, (cell - 8f) / img.Height));
        int cx = i % cols * cell, cy = i / cols * (cell + 14);
        g.DrawImage(img, cx + 4, cy + 4, img.Width * s, img.Height * s);
        g.DrawString(Path.GetFileNameWithoutExtension(files[i]), font, Brushes.White, cx + 2, cy + cell);
    }
    sheet.Save(path, ImageFormat.Png);
}

record AssetMap(List<Atlas> Atlases);
record Atlas(string File, string Folder, string Bg, List<AssetBox> Assets);
record AssetBox(string Name, int[] Box, string? Bg, int? Tol, bool? Ico);
