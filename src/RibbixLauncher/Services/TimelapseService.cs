using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RibbixLauncher.Core;
using RibbixLauncher.Models;

namespace RibbixLauncher.Services;

public sealed record TimelapseResult(string GifPath, string FramesDir, int Frames);

/// <summary>Источник регионов: папка мира или zip-бэкап (записи region/r.X.Z.mca).</summary>
public interface IRegionSource
{
    Stream? Open(int rx, int rz);
}

public sealed class FolderRegions(string worldDir) : IRegionSource
{
    public Stream? Open(int rx, int rz)
    {
        var f = Path.Combine(worldDir, "region", $"r.{rx}.{rz}.mca");
        return File.Exists(f) ? new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
    }
}

public sealed class ZipRegions(ZipArchive zip) : IRegionSource
{
    public Stream? Open(int rx, int rz)
    {
        if (zip.GetEntry($"region/r.{rx}.{rz}.mca") is not { } e) return null;
        var ms = new MemoryStream();
        using (var s = e.Open()) s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}

/// <summary>
/// Вид сверху на мир, как карта: для каждого столбца — верхний блок (Heightmaps.WORLD_SURFACE) и его цвет,
/// с тенью по перепаду высот. Формат чанков 1.13+ (до 1.18 — Level/Sections, после — sections/block_states).
/// </summary>
public static class MapRenderer
{
    /// <summary>Пиксели BGRA size×size, центр — (cx, cz) в блоках.</summary>
    public static byte[] Render(IRegionSource src, int cx, int cz, int size)
    {
        var x0 = cx - size / 2;
        var z0 = cz - size / 2;
        var px = new byte[size * size * 4];
        var heights = new int[size, size];
        var colors = new Color[size, size];
        for (var i = 0; i < size; i++) for (var j = 0; j < size; j++) heights[i, j] = int.MinValue;

        for (var rx = FloorDiv(x0, 512); rx <= FloorDiv(x0 + size - 1, 512); rx++)
        for (var rz = FloorDiv(z0, 512); rz <= FloorDiv(z0 + size - 1, 512); rz++)
        {
            using var s = src.Open(rx, rz);
            if (s is null) continue;
            var region = new byte[s.Length];
            s.ReadExactly(region);
            for (var c = 0; c < 1024; c++)
            {
                var chx = rx * 32 + c % 32;
                var chz = rz * 32 + c / 32;
                if (chx * 16 + 15 < x0 || chx * 16 >= x0 + size || chz * 16 + 15 < z0 || chz * 16 >= z0 + size) continue;
                Dictionary<string, object?>? chunk;
                try { chunk = ReadChunk(region, c); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or ArgumentException) { continue; }
                if (chunk is null) continue;
                DrawChunk(chunk, chx, chz, x0, z0, size, heights, colors);
            }
        }

        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var o = (z * size + x) * 4;
            if (heights[x, z] == int.MinValue) { px[o] = 0x16; px[o + 1] = 0x10; px[o + 2] = 0x0B; px[o + 3] = 255; continue; }
            var c = colors[x, z];
            // Тень как на картах Minecraft: выше северного соседа — светлее, ниже — темнее.
            var north = z > 0 && heights[x, z - 1] != int.MinValue ? heights[x, z - 1] : heights[x, z];
            var k = heights[x, z] > north ? 1.12 : heights[x, z] < north ? 0.82 : 1.0;
            px[o] = Clamp(c.B * k); px[o + 1] = Clamp(c.G * k); px[o + 2] = Clamp(c.R * k); px[o + 3] = 255;
        }
        return px;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    internal static Dictionary<string, object?>? ReadChunk(byte[] region, int index)
    {
        var loc = BinaryPrimitives.ReadInt32BigEndian(region.AsSpan(index * 4, 4));
        var offset = (loc >> 8) * 4096;
        if (loc == 0 || offset + 5 > region.Length) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(region.AsSpan(offset, 4));
        var kind = region[offset + 4];
        if (len <= 1 || offset + 4 + len > region.Length) return null;
        using var raw = new MemoryStream(region, offset + 5, len - 1);
        Stream data = kind switch
        {
            1 => new GZipStream(raw, CompressionMode.Decompress),
            2 => new ZLibStream(raw, CompressionMode.Decompress),
            3 => raw,
            _ => throw new InvalidDataException(),
        };
        using var ms = new MemoryStream();
        using (data) data.CopyTo(ms);
        ms.Position = 0;
        return Nbt.Read(ms);
    }

    private static void DrawChunk(Dictionary<string, object?> chunk, int chx, int chz, int x0, int z0, int size, int[,] heights, Color[,] colors)
    {
        // 1.18+: всё в корне; 1.13–1.17: внутри Level.
        var level = chunk.GetValueOrDefault("Level") as Dictionary<string, object?> ?? chunk;
        var modern = level.ContainsKey("sections");
        var sections = (level.GetValueOrDefault(modern ? "sections" : "Sections") as List<object?>)?.OfType<Dictionary<string, object?>>().ToList();
        var hm = (level.GetValueOrDefault("Heightmaps") as Dictionary<string, object?>)?.GetValueOrDefault("WORLD_SURFACE") as long[];
        if (sections is null || hm is null) return;
        var dataVersion = chunk.GetValueOrDefault("DataVersion") is int dv ? dv : 0;
        var spanning = dataVersion is > 0 and < 2529; // до 20w17a (1.16) значения переходили через границу long
        var minY = modern ? sections.Select(s => Convert.ToInt32(s.GetValueOrDefault("Y") ?? 0)).DefaultIfEmpty(0).Min() * 16 : 0;
        if (modern && minY > -64 && dataVersion >= 2825) minY = -64; // секции под миром могут не храниться

        var bySection = new Dictionary<int, (List<string> Palette, long[]? Data)>();
        foreach (var s in sections)
        {
            var y = Convert.ToInt32(s.GetValueOrDefault("Y") ?? 0);
            List<object?>? palette;
            long[]? states;
            if (modern)
            {
                var bs = s.GetValueOrDefault("block_states") as Dictionary<string, object?>;
                palette = bs?.GetValueOrDefault("palette") as List<object?>;
                states = bs?.GetValueOrDefault("data") as long[];
            }
            else
            {
                palette = s.GetValueOrDefault("Palette") as List<object?>;
                states = s.GetValueOrDefault("BlockStates") as long[];
            }
            if (palette is null) continue;
            bySection[y] = (palette.OfType<Dictionary<string, object?>>().Select(p => p.GetValueOrDefault("Name") as string ?? "").ToList(), states);
        }

        for (var lz = 0; lz < 16; lz++)
        for (var lx = 0; lx < 16; lx++)
        {
            var x = chx * 16 + lx - x0;
            var z = chz * 16 + lz - z0;
            if (x < 0 || z < 0 || x >= size || z >= size) continue;
            var top = (int)Unpack(hm, lz * 16 + lx, 9, spanning) - 1 + minY;
            // Под WORLD_SURFACE может оказаться воздух (стекло, факел) — спускаемся до первого видимого блока.
            for (var y = top; y >= minY && y > top - 24; y--)
            {
                var name = BlockAt(bySection, lx, y, lz, spanning);
                if (name is null || BlockColors.IsInvisible(name)) continue;
                heights[x, z] = y;
                colors[x, z] = BlockColors.For(name);
                break;
            }
        }
    }

    private static string? BlockAt(Dictionary<int, (List<string> Palette, long[]? Data)> sections, int x, int y, int z, bool spanning)
    {
        if (!sections.TryGetValue(FloorDiv(y, 16), out var sec) || sec.Palette.Count == 0) return null;
        if (sec.Palette.Count == 1 || sec.Data is null) return sec.Palette[0];
        var bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(sec.Palette.Count)));
        var i = (int)Unpack(sec.Data, ((y & 15) * 16 + z) * 16 + x, bits, spanning);
        return i < sec.Palette.Count ? sec.Palette[i] : null;
    }

    internal static long Unpack(long[] data, int index, int bits, bool spanning)
    {
        var mask = (1L << bits) - 1;
        if (!spanning)
        {
            var perLong = 64 / bits;
            var l = index / perLong;
            return l < data.Length ? (data[l] >>> ((index % perLong) * bits)) & mask : 0;
        }
        var bit = (long)index * bits;
        var start = (int)(bit / 64);
        var offset = (int)(bit % 64);
        if (start >= data.Length) return 0;
        var v = data[start] >>> offset;
        if (offset + bits > 64 && start + 1 < data.Length) v |= data[start + 1] << (64 - offset);
        return v & mask;
    }
}

/// <summary>Цвета блоков для карты — примерно как на картах Minecraft; неизвестные — по ключевым словам в имени.</summary>
public static class BlockColors
{
    private static readonly Dictionary<string, Color> Exact = new()
    {
        ["grass_block"] = C(0x6A, 0xA8, 0x3E), ["dirt"] = C(0x86, 0x60, 0x43), ["coarse_dirt"] = C(0x77, 0x55, 0x3B), ["podzol"] = C(0x5B, 0x40, 0x1F),
        ["water"] = C(0x3F, 0x76, 0xE4), ["lava"] = C(0xE8, 0x6A, 0x0F), ["sand"] = C(0xDB, 0xD3, 0xA0), ["red_sand"] = C(0xBE, 0x66, 0x21),
        ["gravel"] = C(0x88, 0x80, 0x7E), ["stone"] = C(0x7D, 0x7D, 0x7D), ["snow"] = C(0xF5, 0xFA, 0xFA), ["snow_block"] = C(0xF5, 0xFA, 0xFA),
        ["ice"] = C(0x9C, 0xC1, 0xF7), ["packed_ice"] = C(0x8D, 0xB4, 0xF2), ["clay"] = C(0xA0, 0xA6, 0xB3), ["mycelium"] = C(0x6F, 0x63, 0x69),
        ["netherrack"] = C(0x6F, 0x36, 0x34), ["end_stone"] = C(0xDB, 0xDE, 0x9E), ["obsidian"] = C(0x14, 0x12, 0x1E), ["bedrock"] = C(0x55, 0x55, 0x55),
        ["glass"] = C(0xC0, 0xE4, 0xEC), ["torch"] = C(0xFF, 0xD8, 0x60), ["farmland"] = C(0x6B, 0x47, 0x2B), ["dirt_path"] = C(0x94, 0x7A, 0x41),
        ["moss_block"] = C(0x59, 0x6E, 0x2D), ["mud"] = C(0x3C, 0x3A, 0x3D), ["calcite"] = C(0xDF, 0xE0, 0xDC), ["deepslate"] = C(0x4D, 0x4D, 0x51),
        ["bricks"] = C(0x96, 0x61, 0x53), ["bookshelf"] = C(0x6B, 0x58, 0x39), ["hay_block"] = C(0xA6, 0x88, 0x19), ["pumpkin"] = C(0xC6, 0x76, 0x18),
        ["melon"] = C(0x6F, 0x91, 0x1F), ["cactus"] = C(0x55, 0x80, 0x2A), ["sugar_cane"] = C(0x8E, 0xBC, 0x5E), ["lily_pad"] = C(0x20, 0x80, 0x30),
        ["seagrass"] = C(0x3F, 0x76, 0xE4), ["kelp"] = C(0x3F, 0x76, 0xE4), ["tall_seagrass"] = C(0x3F, 0x76, 0xE4), ["bubble_column"] = C(0x3F, 0x76, 0xE4),
    };

    private static readonly (string Key, Color Color)[] Dyes =
    {
        ("white", C(0xE9, 0xEC, 0xEC)), ("light_gray", C(0x8E, 0x8E, 0x86)), ("gray", C(0x3E, 0x44, 0x47)), ("black", C(0x15, 0x15, 0x1A)),
        ("brown", C(0x72, 0x47, 0x28)), ("red", C(0xA1, 0x27, 0x22)), ("orange", C(0xF0, 0x76, 0x13)), ("yellow", C(0xF8, 0xC5, 0x27)),
        ("lime", C(0x70, 0xB9, 0x19)), ("green", C(0x54, 0x6D, 0x1B)), ("light_blue", C(0x3A, 0xAF, 0xD9)), ("cyan", C(0x15, 0x89, 0x91)),
        ("blue", C(0x35, 0x39, 0x9D)), ("purple", C(0x79, 0x2A, 0xAC)), ("magenta", C(0xBD, 0x44, 0xB3)), ("pink", C(0xED, 0x8D, 0xAC)),
    };

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public static bool IsInvisible(string name) => name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or "minecraft:barrier" or "minecraft:light"
        || name.EndsWith("glass_pane") || name.EndsWith("_button") || name.EndsWith("tripwire") || name.EndsWith("string");

    public static Color For(string fullName)
    {
        var n = fullName.StartsWith("minecraft:") ? fullName[10..] : fullName[(fullName.IndexOf(':') + 1)..];
        if (Exact.TryGetValue(n, out var c)) return c;
        if (n.Contains("wool") || n.Contains("concrete") || n.Contains("terracotta") || n.Contains("carpet") || n.Contains("stained") || n.Contains("bed"))
            foreach (var (key, color) in Dyes)
                if (n.StartsWith(key + "_")) return n.Contains("terracotta") ? Mix(color, C(0x98, 0x5E, 0x43), 0.4) : color;
        if (n.Contains("leaves") || n.Contains("vine") || n == "short_grass" || n == "grass" || n.Contains("fern") || n.Contains("bush")) return C(0x3B, 0x7D, 0x21);
        if (n.Contains("log") || n.Contains("wood") || n.Contains("stem") || n.Contains("hyphae")) return C(0x66, 0x4F, 0x33);
        if (n.Contains("planks") || n.Contains("stairs") && n.Contains("oak") || n.Contains("slab") && n.Contains("oak") || n.Contains("fence") || n.Contains("door")) return C(0xA2, 0x82, 0x4E);
        if (n.Contains("cherry")) return C(0xE8, 0xB4, 0xC8);
        if (n.Contains("sandstone")) return C(0xD8, 0xCB, 0x9B);
        if (n.Contains("quartz")) return C(0xEC, 0xE6, 0xDF);
        if (n.Contains("copper")) return C(0xC0, 0x6C, 0x4F);
        if (n.Contains("prismarine")) return C(0x5C, 0xA8, 0x98);
        if (n.Contains("nether")) return C(0x60, 0x26, 0x2C);
        if (n.Contains("deepslate") || n.Contains("blackstone") || n.Contains("basalt")) return C(0x45, 0x45, 0x4A);
        if (n.Contains("stone") || n.Contains("andesite") || n.Contains("diorite") || n.Contains("granite") || n.Contains("cobble") || n.Contains("ore") || n.Contains("tuff")) return C(0x84, 0x84, 0x84);
        if (n.Contains("glass")) return C(0xC0, 0xE4, 0xEC);
        if (n.Contains("flower") || n.Contains("tulip") || n.Contains("rose") || n.Contains("poppy") || n.Contains("dandelion") || n.Contains("orchid")) return C(0xC8, 0x3C, 0x3C);
        if (n.Contains("wheat") || n.Contains("carrots") || n.Contains("potatoes") || n.Contains("beetroots")) return C(0x9C, 0xB0, 0x3A);
        if (n.Contains("snow")) return C(0xF5, 0xFA, 0xFA);
        if (n.Contains("ice")) return C(0x9C, 0xC1, 0xF7);
        if (n.Contains("iron")) return C(0xA7, 0xA7, 0xA7);
        if (n.Contains("gold")) return C(0xF5, 0xD1, 0x3C);
        if (n.Contains("diamond")) return C(0x5C, 0xDB, 0xD5);
        // Неизвестный (в том числе из модов) — стабильный цвет из имени.
        var h = (uint)n.Aggregate(17, (a, ch) => a * 31 + ch);
        return C((byte)(90 + h % 100), (byte)(90 + (h >> 8) % 100), (byte)(90 + (h >> 16) % 100));
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R * (1 - t) + b.R * t), (byte)(a.G * (1 - t) + b.G * t), (byte)(a.B * (1 - t) + b.B * t));
}

/// <summary>
/// Анимированный GIF без сторонних библиотек: общая палитра 6×6×6 + 40 оттенков серого, LZW, петля NETSCAPE2.0.
/// Карта и подписи хорошо ложатся в такую палитру.
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _out;
    private readonly int _w, _h;
    private static readonly byte[] Palette = BuildPalette();

    public GifWriter(Stream output, int width, int height)
    {
        _out = output;
        _w = width;
        _h = height;
        var hdr = new BinaryWriter(_out);
        hdr.Write("GIF89a"u8);
        hdr.Write((ushort)width);
        hdr.Write((ushort)height);
        hdr.Write((byte)0xF7); // глобальная палитра, 8 бит, 256 цветов
        hdr.Write((byte)0);
        hdr.Write((byte)0);
        hdr.Write(Palette);
        hdr.Write(new byte[] { 0x21, 0xFF, 0x0B });
        hdr.Write("NETSCAPE2.0"u8);
        hdr.Write(new byte[] { 3, 1, 0, 0, 0 }); // бесконечный повтор
    }

    private static byte[] BuildPalette()
    {
        var p = new byte[768];
        var i = 0;
        for (var r = 0; r < 6; r++) for (var g = 0; g < 6; g++) for (var b = 0; b < 6; b++)
        {
            p[i++] = (byte)(r * 51); p[i++] = (byte)(g * 51); p[i++] = (byte)(b * 51);
        }
        for (var k = 0; k < 40; k++)
        {
            var v = (byte)(k * 255 / 39);
            p[i++] = v; p[i++] = v; p[i++] = v;
        }
        return p;
    }

    internal static byte Index(byte r, byte g, byte b)
    {
        // Почти серые — в шкалу серого (точнее для текста и камня), остальное — в куб.
        if (Math.Abs(r - g) < 10 && Math.Abs(g - b) < 10) return (byte)(216 + (r + g + b) / 3 * 39 / 255);
        return (byte)((r + 25) / 51 * 36 + (g + 25) / 51 * 6 + (b + 25) / 51);
    }

    /// <summary>Кадр из BGRA-пикселей, задержка — в сотых секунды.</summary>
    public void AddFrame(byte[] bgra, int delayCs)
    {
        var w = new BinaryWriter(_out);
        w.Write(new byte[] { 0x21, 0xF9, 4, 0 });
        w.Write((ushort)delayCs);
        w.Write(new byte[] { 0, 0 });
        w.Write((byte)0x2C);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)_w); w.Write((ushort)_h);
        w.Write((byte)0);
        var idx = new byte[_w * _h];
        for (var i = 0; i < idx.Length; i++) idx[i] = Index(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);
        w.Write((byte)8);
        WriteLzw(w, idx);
    }

    private static void WriteLzw(BinaryWriter w, byte[] data)
    {
        const int clear = 256, end = 257;
        var block = new List<byte>(255);
        int acc = 0, accBits = 0, codeSize = 9, next = 258;
        var dict = new Dictionary<int, int>();
        void Emit(int code)
        {
            acc |= code << accBits;
            accBits += codeSize;
            while (accBits >= 8)
            {
                block.Add((byte)acc);
                acc >>= 8;
                accBits -= 8;
                if (block.Count == 255) { w.Write((byte)255); w.Write(block.ToArray()); block.Clear(); }
            }
        }
        Emit(clear);
        var prefix = data.Length > 0 ? data[0] : 0;
        for (var i = 1; i < data.Length; i++)
        {
            var key = (prefix << 8) | data[i];
            if (dict.TryGetValue(key, out var code)) { prefix = code; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clear);
                dict.Clear();
                next = 258;
                codeSize = 9;
            }
            prefix = data[i];
        }
        Emit(prefix);
        Emit(end);
        if (accBits > 0) block.Add((byte)acc);
        if (block.Count > 0) { w.Write((byte)block.Count); w.Write(block.ToArray()); }
        w.Write((byte)0);
    }

    public void Dispose()
    {
        _out.WriteByte(0x3B);
        _out.Flush();
    }
}

/// <summary>
/// Таймлапс стройки из бэкапов мира: вид сверху на каждый бэкап и текущий мир — по кадру, подписи с датой,
/// GIF 720×1280 (вертикальный, для сторис) и PNG-кадры для монтажа в CapCut/TikTok.
/// </summary>
public static class TimelapseService
{
    public const int FrameW = 720, FrameH = 1280, MapPx = 640;

    public static TimelapseResult Create(Instance inst, WorldInfo world, IReadOnlyList<WorldBackup> backups, int areaBlocks, string outDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var worldDir = Path.Combine(inst.GameDirectory, "saves", world.Folder);
        var (cx, cz) = CenterOf(Path.Combine(worldDir, "level.dat"));
        var sources = backups.OrderBy(b => b.Created).Select(b => (b.Created, (string?)b.Path)).Append((DateTime.Now, null)).ToList();

        Directory.CreateDirectory(outDir);
        var framesDir = Path.Combine(outDir, L.T("кадры"));
        Directory.CreateDirectory(framesDir);
        var gif = Path.Combine(outDir, $"{Safe(world.Name)}-timelapse.gif");
        using (var fs = File.Create(gif))
        using (var writer = new GifWriter(fs, FrameW, FrameH))
        {
            for (var i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(L.T($"Рисуем кадр {i + 1} из {sources.Count}"));
                var (date, zipPath) = sources[i];
                byte[] map;
                if (zipPath is null) map = MapRenderer.Render(new FolderRegions(worldDir), cx, cz, areaBlocks);
                else
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    map = MapRenderer.Render(new ZipRegions(zip), cx, cz, areaBlocks);
                }
                var frame = Compose(map, areaBlocks, world.Name, date, i, sources.Count);
                YearRecapService.Save(frame, Path.Combine(framesDir, $"{i + 1:000}.png"));
                var px = new byte[FrameW * FrameH * 4];
                frame.CopyPixels(px, FrameW * 4, 0);
                writer.AddFrame(px, i == sources.Count - 1 ? 250 : 60);
            }
        }
        return new TimelapseResult(gif, framesDir, sources.Count);
    }

    private static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars()));

    /// <summary>Центр — где игрок сейчас (там обычно и стройка), иначе точка спавна.</summary>
    internal static (int, int) CenterOf(string levelDat)
    {
        try
        {
            var data = Nbt.ReadGzip(levelDat)?.GetValueOrDefault("Data") as Dictionary<string, object?>;
            if ((data?.GetValueOrDefault("Player") as Dictionary<string, object?>)?.GetValueOrDefault("Pos") is List<object?> { Count: 3 } pos)
                return ((int)Math.Floor((double)pos[0]!), (int)Math.Floor((double)pos[2]!));
            if (data?.GetValueOrDefault("SpawnX") is int sx && data.GetValueOrDefault("SpawnZ") is int sz) return (sx, sz);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or InvalidCastException) { }
        return (0, 0);
    }

    private static BitmapSource Compose(byte[] map, int size, string worldName, DateTime date, int index, int count)
    {
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, map, size * 4);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x05, 0x2E, 0x16), Color.FromRgb(0x0B, 0x12, 0x20), 90), null, new Rect(0, 0, FrameW, FrameH));
            var left = (FrameW - MapPx) / 2.0;
            const double top = 250;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), null, new Rect(left - 6, top - 6, MapPx + 12, MapPx + 12), 18, 18);
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(bmp, new Rect(left, top, MapPx, MapPx));
            Text(dc, L.T("🐸 Как рос мой мир"), 52, FontWeights.Black, Colors.White, 90);
            Text(dc, worldName, 34, FontWeights.SemiBold, Color.FromRgb(0xA7, 0xF3, 0xD0), 165);
            Text(dc, date.ToString("d MMMM yyyy", L.Culture), 56, FontWeights.Bold, Colors.White, top + MapPx + 50);
            Text(dc, index == count - 1 ? L.T("сейчас") : L.T($"кадр {index + 1} из {count}"), 32, FontWeights.Normal, Color.FromRgb(0xB6, 0xC2, 0xD2), top + MapPx + 130);
            Text(dc, L.T($"{size}×{size} блоков · вид сверху"), 28, FontWeights.Normal, Color.FromRgb(0x93, 0xA3, 0xB8), top + MapPx + 180);
            var bar = (FrameW - 160.0) * (index + 1) / count;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new Rect(80, FrameH - 150, FrameW - 160, 14), 7, 7);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), null, new Rect(80, FrameH - 150, bar, 14), 7, 7);
            Text(dc, "Ribbix Launcher", 30, FontWeights.SemiBold, Color.FromRgb(0xA7, 0xF3, 0xD0), FrameH - 110);
        }
        var rtb = new RenderTargetBitmap(FrameW, FrameH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Text(DrawingContext dc, string s, double size, FontWeight weight, Color color, double y)
    {
        var ft = new FormattedText(s, L.Culture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0) { MaxTextWidth = FrameW - 80, TextAlignment = TextAlignment.Center, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(40, y));
    }
}
