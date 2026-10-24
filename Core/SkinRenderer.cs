using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrogLauncher.Core;

/// <summary>Предпросмотр скина «вид спереди»: голова, тело, руки, ноги + второй слой (шляпа/куртка/рукава).</summary>
public static class SkinRenderer
{
    public static ImageSource? Render(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = BitmapCacheOption.OnLoad; // не держим файл открытым
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();

            if (bi.PixelWidth != 64 || (bi.PixelHeight != 64 && bi.PixelHeight != 32)) return null;
            bool legacy = bi.PixelHeight == 32; // старый формат 64x32: левые части = зеркало правых

            const int scale = 10;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));

                void Part(int sx, int sy, int w, int h, double dx, double dy, bool mirror = false)
                {
                    var crop = new CroppedBitmap(bi, new Int32Rect(sx, sy, w, h));
                    if (mirror) dc.PushTransform(new ScaleTransform(-1, 1, dx + w / 2.0, 0));
                    dc.DrawImage(crop, new Rect(dx, dy, w, h));
                    if (mirror) dc.Pop();
                }

                // базовый слой (передние грани)
                Part(8, 8, 8, 8, 4, 0);                      // голова
                Part(20, 20, 8, 12, 4, 8);                   // тело
                Part(44, 20, 4, 12, 0, 8);                   // правая рука
                if (legacy) Part(44, 20, 4, 12, 12, 8, true); else Part(36, 52, 4, 12, 12, 8);   // левая рука
                Part(4, 20, 4, 12, 4, 20);                   // правая нога
                if (legacy) Part(4, 20, 4, 12, 8, 20, true); else Part(20, 52, 4, 12, 8, 20);    // левая нога

                // второй слой
                Part(40, 8, 8, 8, 4, 0);                     // шляпа
                if (!legacy)
                {
                    Part(20, 36, 8, 12, 4, 8);               // куртка
                    Part(44, 36, 4, 12, 0, 8);               // правый рукав
                    Part(52, 52, 4, 12, 12, 8);              // левый рукав
                    Part(4, 36, 4, 12, 4, 20);               // правая штанина
                    Part(4, 52, 4, 12, 8, 20);               // левая штанина
                }
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(16 * scale, 32 * scale, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Log.Warn("Предпросмотр скина: " + ex.Message);
            return null;
        }
    }
}
