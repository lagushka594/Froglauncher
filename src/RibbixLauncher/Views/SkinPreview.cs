using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RibbixLauncher.Views;

/// <summary>
/// Персонаж спереди по развёртке скина 64×64 (или старого 64×32): голова, тело, руки, ноги и второй слой
/// (шапка, куртка). Пиксели — без сглаживания. Slim — руки Alex шириной 3 px.
/// </summary>
public sealed class SkinPreview : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(SkinPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SkinPreview)d)._bitmap = null));
    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(nameof(Slim), typeof(bool), typeof(SkinPreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool Slim { get => (bool)GetValue(SlimProperty); set => SetValue(SlimProperty, value); }

    private BitmapSource? _bitmap;

    public SkinPreview() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

    private BitmapSource? Bitmap()
    {
        if (_bitmap is not null || Source is not { Length: > 0 } path || !File.Exists(path)) return _bitmap;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            _bitmap = b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { }
        return _bitmap;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Bitmap() is not { } bmp) return;
        var modern = bmp.PixelHeight >= 64;
        // Фигура 16×32 «пикселей скина».
        var px = Math.Min(ActualWidth / 16, ActualHeight / 32);
        var ox = (ActualWidth - px * 16) / 2;
        var oy = (ActualHeight - px * 32) / 2;
        var arm = Slim ? 3 : 4;
        void Part(int sx, int sy, int w, int h, double dx, double dy, bool flip = false)
        {
            if (sx + w > bmp.PixelWidth || sy + h > bmp.PixelHeight) return;
            var crop = new CroppedBitmap(bmp, new Int32Rect(sx, sy, w, h));
            var rect = new Rect(ox + dx * px, oy + dy * px, w * px, h * px);
            if (flip)
            {
                dc.PushTransform(new ScaleTransform(-1, 1, rect.X + rect.Width / 2, 0));
                dc.DrawImage(crop, rect);
                dc.Pop();
            }
            else dc.DrawImage(crop, rect);
        }

        // Основной слой: голова, тело, правая рука (слева на картинке), левая рука, ноги.
        Part(8, 8, 8, 8, 4, 0);
        Part(20, 20, 8, 12, 4, 8);
        Part(44, 20, arm, 12, 4 - arm, 8);
        if (modern) Part(36, 52, arm, 12, 12, 8); else Part(44, 20, arm, 12, 12, 8, flip: true);
        Part(4, 20, 4, 12, 4, 20);
        if (modern) Part(20, 52, 4, 12, 8, 20); else Part(4, 20, 4, 12, 8, 20, flip: true);

        // Второй слой (только в 64×64; у старых — только шапка).
        Part(40, 8, 8, 8, 4, 0);
        if (!modern) return;
        Part(20, 36, 8, 12, 4, 8);
        Part(44, 36, arm, 12, 4 - arm, 8);
        Part(52, 52, arm, 12, 12, 8);
        Part(4, 36, 4, 12, 4, 20);
        Part(4, 52, 4, 12, 8, 20);
    }
}
