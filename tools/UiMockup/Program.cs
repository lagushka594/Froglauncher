// UiMockup <макет.xaml> <выход.png> [ширина высота] — отрисовать макет со стилями лаунчера.
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    [STAThread]
    static int Main(string[] a)
    {
        var app = new Application();
        _ = typeof(RibbixLauncher.Views.PixelBorder); // загрузить сборку лаунчера для pack://…;component
        foreach (var dict in new[] { "Themes/Theme.xaml", "Themes/Pixel.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/RibbixLauncher;component/{dict}") });
        int w = a.Length > 2 ? int.Parse(a[2]) : 1180, h = a.Length > 3 ? int.Parse(a[3]) : 740;
        var root = (FrameworkElement)XamlReader.Parse(File.ReadAllText(a[0]));
        root.Width = w;
        root.Height = h;
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        // Картинкам и шрифтам нужен один проход диспетчера.
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(a[1]);
        enc.Save(fs);
        return 0;
    }
}
