using RibbixLauncher.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RibbixLauncher.Services;

public sealed record Screenshot(string Path, string InstanceName, string GameVersion, DateTime Taken);

/// <summary>
/// Скриншоты всех версий и карточка «Поделиться» 1080×1920 (формат TikTok/Reels/Shorts):
/// скриншот, логотип, ник, версия и дата. Рендер — WPF RenderTargetBitmap, без сторонних библиотек.
/// </summary>
public static class ShareCardService
{
    public const int Width = 1080, Height = 1920;

    public static string OutputDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RibbixLauncher");

    public static IReadOnlyList<Screenshot> List(IEnumerable<Models.Instance> instances)
    {
        var result = new List<Screenshot>();
        foreach (var inst in instances)
        {
            var dir = System.IO.Path.Combine(inst.GameDirectory, "screenshots");
            if (!Directory.Exists(dir)) continue;
            result.AddRange(new DirectoryInfo(dir).GetFiles("*.png")
                .Select(f => new Screenshot(f.FullName, inst.Name, inst.GameVersion, f.LastWriteTime)));
        }
        return result.OrderByDescending(s => s.Taken).ToList();
    }

    public static BitmapImage LoadThumb(string path, int width)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // не держим файл открытым — его можно удалить
        bmp.DecodePixelWidth = width;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Собрать карточку и сохранить PNG в «Изображения\RibbixLauncher». Возвращает путь.</summary>
    public static string Render(Screenshot shot, string nick, string accent)
    {
        var accentColor = (Color)ColorConverter.ConvertFromString(accent);
        var root = new Grid { Width = Width, Height = Height };
        root.Background = new LinearGradientBrush(Color.FromRgb(0x0B, 0x12, 0x20), Color.FromRgb(0x08, 0x1A, 0x14), 90);

        // Размытый фон из самого скриншота.
        var photo = LoadThumb(shot.Path, 1600);
        root.Children.Add(new Image
        {
            Source = photo, Stretch = Stretch.UniformToFill, Opacity = 0.35,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 60 },
        });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(70, 0, 70, 0) };
        stack.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/main/logo.png")),
            Height = 300, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 70),
        });
        var photoHeight = Math.Min(1000, 928.0 * photo.PixelHeight / Math.Max(1, photo.PixelWidth));
        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(36),
            BorderBrush = new SolidColorBrush(accentColor), BorderThickness = new Thickness(6),
            Child = new Image
            {
                Source = photo, Stretch = Stretch.UniformToFill, Width = 928, Height = photoHeight,
                // Border не обрезает содержимое по скруглению — режем сами.
                Clip = new RectangleGeometry(new Rect(0, 0, 928, photoHeight), 30, 30),
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = accentColor, BlurRadius = 70, ShadowDepth = 0, Opacity = 0.75 },
        });
        stack.Children.Add(Text(nick, 92, FontWeights.Black, Colors.White, 70));
        stack.Children.Add(Text($"Minecraft {shot.GameVersion} · {shot.InstanceName}", 44, FontWeights.SemiBold, accentColor, 18));
        stack.Children.Add(Text(shot.Taken.ToString("d MMMM yyyy", L.Culture), 38, FontWeights.Normal,
            Color.FromRgb(0x93, 0xA3, 0xB8), 12));
        stack.Children.Add(Text("🐸 Ribbix", 40, FontWeights.Bold, Color.FromRgb(0xA7, 0xF3, 0xD0), 90));
        root.Children.Add(stack);

        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        Directory.CreateDirectory(OutputDir);
        var file = System.IO.Path.Combine(OutputDir, $"ribbix-card-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, Color color, double top) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(color),
        FontFamily = new FontFamily("Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };
}
