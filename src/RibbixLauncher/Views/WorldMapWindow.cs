using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using RibbixLauncher.Core;
using RibbixLauncher.Services;

namespace RibbixLauncher.Views;

/// <summary>
/// Карта мира: вид сверху (MapRenderer, как в таймлапсе), приближение 128…2048 блоков, перемещение стрелками
/// и перетаскиванием, отметки игрока и спавна. Мир только читается — можно смотреть и во время игры.
/// </summary>
public sealed class WorldMapWindow : Window
{
    private static readonly int[] Sizes = { 128, 256, 512, 1024, 2048 };
    private readonly string _worldDir;
    private readonly (int X, int Z)? _player, _spawn;
    private int _cx, _cz, _zoom = 1;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _marks = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.White, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _cts;
    private Point? _drag;

    public WorldMapWindow(string worldDir, string name)
    {
        _worldDir = worldDir;
        (_player, _spawn) = ReadPositions(Path.Combine(worldDir, "level.dat"));
        (_cx, _cz) = _player ?? _spawn ?? (0, 0);
        Title = L.T($"Карта мира — {name}");
        Width = 900;
        Height = 820;
        Background = (Brush)Application.Current.Resources["BgBrush"];
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        var bar = new WrapPanel { Margin = new Thickness(12) };
        Button B(string text, Action click, string? tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, Style = (Style)Application.Current.Resources["GhostButton"] };
            b.Click += (_, _) => click();
            bar.Children.Add(b);
            return b;
        }
        B("➖", () => Zoom(+1), L.T("Отдалить"));
        B("➕", () => Zoom(-1), L.T("Приблизить"));
        B("⬅", () => Move(-1, 0));
        B("⬆", () => Move(0, -1));
        B("⬇", () => Move(0, 1));
        B("➡", () => Move(1, 0));
        if (_player is { } p) B(L.T("🐸 К игроку"), () => { (_cx, _cz) = p; Render(); });
        if (_spawn is { } s) B(L.T("🏠 К спавну"), () => { (_cx, _cz) = s; Render(); });
        bar.Children.Add(_status);

        var view = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16)), ClipToBounds = true };
        var square = new Grid();
        square.Children.Add(_image);
        square.Children.Add(_marks);
        view.Children.Add(new Viewbox { Child = square, Stretch = Stretch.Uniform });
        view.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(view); view.CaptureMouse(); };
        view.MouseLeftButtonUp += (_, e) =>
        {
            view.ReleaseMouseCapture();
            if (_drag is not { } start) return;
            var d = e.GetPosition(view) - start;
            _drag = null;
            var scale = Sizes[_zoom] / Math.Min(view.ActualWidth, view.ActualHeight);
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 4) return;
            _cx -= (int)(d.X * scale);
            _cz -= (int)(d.Y * scale);
            Render();
        };
        view.MouseWheel += (_, e) => Zoom(e.Delta > 0 ? -1 : +1);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(view);
        Content = root;
        KeyDown += OnKey;
        Loaded += (_, _) => Render();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.A: Move(-1, 0); break;
            case Key.Right or Key.D: Move(1, 0); break;
            case Key.Up or Key.W: Move(0, -1); break;
            case Key.Down or Key.S: Move(0, 1); break;
            case Key.Add or Key.OemPlus: Zoom(-1); break;
            case Key.Subtract or Key.OemMinus: Zoom(+1); break;
        }
    }

    private void Zoom(int d)
    {
        var z = Math.Clamp(_zoom + d, 0, Sizes.Length - 1);
        if (z == _zoom) return;
        _zoom = z;
        Render();
    }

    private void Move(int dx, int dz)
    {
        _cx += dx * Sizes[_zoom] / 2;
        _cz += dz * Sizes[_zoom] / 2;
        Render();
    }

    private async void Render()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int size = Sizes[_zoom], cx = _cx, cz = _cz;
        _status.Text = L.T($"Рисуем… {size}×{size} блоков вокруг {cx}, {cz}");
        BitmapSource bmp;
        try
        {
            bmp = await Task.Run(() =>
            {
                var px = MapRenderer.Render(new FolderRegions(_worldDir), cx, cz, size);
                var b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
                b.Freeze();
                return b;
            }, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            if (!cts.IsCancellationRequested) _status.Text = ex.Message;
            return;
        }
        if (cts.IsCancellationRequested) return;
        _image.Source = bmp;
        _image.Width = _image.Height = size;
        _marks.Width = _marks.Height = size;
        _marks.Children.Clear();
        void Mark((int X, int Z)? at, Color color, string tip)
        {
            if (at is not { } p) return;
            double x = p.X - (cx - size / 2.0), z = p.Z - (cz - size / 2.0);
            if (x < 0 || z < 0 || x > size || z > size) return;
            var r = Math.Max(4, size / 64.0);
            var dot = new Rectangle { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(color), Stroke = Brushes.Black, StrokeThickness = Math.Max(1, r / 3), ToolTip = tip };
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, z - r);
            _marks.Children.Add(dot);
        }
        Mark(_spawn, Color.FromRgb(0xFA, 0xCC, 0x15), L.T("Спавн"));
        Mark(_player, Color.FromRgb(0x22, 0xC5, 0x5E), L.T("Вы"));
        _status.Text = L.T($"{size}×{size} блоков · центр {cx}, {cz} · 🟩 вы · 🟨 спавн · колесо — приближение, тяни — двигать");
    }

    internal static ((int, int)? Player, (int, int)? Spawn) ReadPositions(string levelDat)
    {
        try
        {
            var data = Nbt.ReadGzip(levelDat)?.GetValueOrDefault("Data") as Dictionary<string, object?>;
            (int, int)? player = null, spawn = null;
            if ((data?.GetValueOrDefault("Player") as Dictionary<string, object?>)?.GetValueOrDefault("Pos") is List<object?> { Count: 3 } pos)
                player = ((int)Math.Floor((double)pos[0]!), (int)Math.Floor((double)pos[2]!));
            if (data?.GetValueOrDefault("SpawnX") is int sx && data.GetValueOrDefault("SpawnZ") is int sz) spawn = (sx, sz);
            return (player, spawn);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or InvalidCastException)
        {
            return (null, null);
        }
    }
}
