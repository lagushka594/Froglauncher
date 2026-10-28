using System.Windows;
using System.Windows.Media;
using RibbixLauncher.Services;

namespace RibbixLauncher.Views;

/// <summary>
/// Сезонные эффекты поверх окна (клики проходят насквозь): весна — лепестки сакуры, лето — блики на воде и стрекоза
/// раз в 30 секунд, осень — падающие листья и туман внизу, зима — снег с ветром и иней по краям, Новый год — снег
/// и мигающие гирлянды. Рисуется в OnRender ~30 кадров/с; пауза, когда окно скрыто или идёт игра.
/// </summary>
public sealed class SeasonEffects : FrameworkElement
{
    private struct P
    {
        public double X, Y, Vx, Vy, Rot, VRot, Size, Phase;
        public Brush Fill;
    }

    public static readonly DependencyProperty SeasonProperty = DependencyProperty.Register(
        nameof(Season), typeof(Season?), typeof(SeasonEffects), new PropertyMetadata(null, (d, _) => ((SeasonEffects)d).Reset()));

    public Season? Season { get => (Season?)GetValue(SeasonProperty); set => SetValue(SeasonProperty, value); }

    /// <summary>Игра запущена или анимации выключены в настройках.</summary>
    public static readonly DependencyProperty PausedProperty = DependencyProperty.Register(
        nameof(Paused), typeof(bool), typeof(SeasonEffects), new PropertyMetadata(false, (d, _) => ((SeasonEffects)d).UpdateRunning()));

    public bool Paused { get => (bool)GetValue(PausedProperty); set => SetValue(PausedProperty, value); }

    private readonly Random _rnd = new();
    private P[] _p = Array.Empty<P>();
    private DateTime _last = DateTime.Now;
    private TimeSpan _renderAcc;
    private bool _running;
    private double _time, _dragonflyAt = 8, _dragonflyT = -1;

    private static readonly Geometry Petal = Geometry.Parse("M0,-6 C4,-4 4,3 0,6 C-4,3 -4,-4 0,-6 Z");
    private static readonly Geometry Leaf = Geometry.Parse(
        "M0,-8 L2,-3 L7,-4 L4,0 L7,3 L2,2 L1,7 L-1,7 L-2,2 L-7,3 L-4,0 L-7,-4 L-2,-3 Z");
    private static readonly Brush[] PetalFills = Frozen("#FBCFE8", "#F9A8D4", "#FFE4F1", "#F472B6");
    private static readonly Brush[] LeafFills = Frozen("#F59E0B", "#DC2626", "#EA580C", "#B45309", "#FBBF24");
    private static readonly Brush[] BulbFills = Frozen("#EF4444", "#22C55E", "#FACC15", "#3B82F6");
    private static readonly Brush Snow = Frozen("#F0F9FF")[0];
    private static readonly Pen Wire = new(Frozen("#1F2937")[0], 1.5);

    public SeasonEffects()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => { Reset(); UpdateRunning(); };
        SizeChanged += (_, _) => Reset();
    }

    private static Brush[] Frozen(params string[] hex) => hex.Select(h =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    private int CountFor(Season? s) => s switch
    {
        Services.Season.Spring => 28,
        Services.Season.Summer => 22,
        Services.Season.Autumn => 24,
        Services.Season.Winter => 70,
        Services.Season.NewYear => 55,
        _ => 0,
    };

    private void Reset()
    {
        var n = CountFor(Season);
        _p = new P[n];
        for (var i = 0; i < n; i++) _p[i] = Spawn(randomY: true);
        UpdateRunning();
        InvalidateVisual();
    }

    private P Spawn(bool randomY)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        var p = new P { X = _rnd.NextDouble() * w, Phase = _rnd.NextDouble() * Math.PI * 2, Rot = _rnd.NextDouble() * 360 };
        switch (Season)
        {
            case Services.Season.Spring:
                p.Y = randomY ? _rnd.NextDouble() * h : -10; p.Vx = 15 + _rnd.NextDouble() * 20; p.Vy = 20 + _rnd.NextDouble() * 25;
                p.VRot = 40 + _rnd.NextDouble() * 80; p.Size = 0.8 + _rnd.NextDouble() * 0.7; p.Fill = PetalFills[_rnd.Next(PetalFills.Length)];
                break;
            case Services.Season.Autumn:
                p.Y = randomY ? _rnd.NextDouble() * h : -12; p.Vx = -10 + _rnd.NextDouble() * 20; p.Vy = 25 + _rnd.NextDouble() * 30;
                p.VRot = -90 + _rnd.NextDouble() * 180; p.Size = 0.9 + _rnd.NextDouble() * 0.9; p.Fill = LeafFills[_rnd.Next(LeafFills.Length)];
                break;
            case Services.Season.Summer:
                // Блики на воде — нижние 40% окна.
                p.Y = h * (0.6 + _rnd.NextDouble() * 0.38); p.Size = 1.5 + _rnd.NextDouble() * 2.5; p.VRot = 1 + _rnd.NextDouble() * 2;
                break;
            default:
                p.Y = randomY ? _rnd.NextDouble() * h : -5; p.Vx = 8 + _rnd.NextDouble() * 10; p.Vy = 25 + _rnd.NextDouble() * 35;
                p.Size = 1 + _rnd.NextDouble() * 2.6;
                break;
        }
        return p;
    }

    private void UpdateRunning()
    {
        var want = IsVisible && !Paused && Season is not null && IsLoaded;
        if (want && !_running)
        {
            _last = DateTime.Now;
            CompositionTarget.Rendering += OnFrame;
            _running = true;
        }
        else if (!want) Stop();
        InvalidateVisual();
    }

    private void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var dt = Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        _renderAcc += TimeSpan.FromSeconds(dt);
        if (_renderAcc < TimeSpan.FromMilliseconds(33)) return; // ~30 кадров/с хватает и бережёт CPU
        dt = _renderAcc.TotalSeconds;
        _renderAcc = TimeSpan.Zero;
        Step(dt);
        InvalidateVisual();
    }

    private void Step(double dt)
    {
        _time += dt;
        double w = ActualWidth, h = ActualHeight;
        var wind = Math.Sin(_time * 0.3) * 18;
        for (var i = 0; i < _p.Length; i++)
        {
            ref var p = ref _p[i];
            if (Season == Services.Season.Summer) { p.Phase += dt * p.VRot; continue; }
            p.X += (p.Vx + wind + Math.Sin(_time * 1.5 + p.Phase) * 12) * dt;
            p.Y += p.Vy * dt;
            p.Rot += p.VRot * dt;
            if (p.Y > h + 12 || p.X > w + 20 || p.X < -20) p = Spawn(randomY: false);
        }
        // Стрекоза раз в 30 секунд пролетает через окно за ~6 секунд.
        if (Season == Services.Season.Summer)
        {
            if (_dragonflyT < 0 && _time >= _dragonflyAt) _dragonflyT = 0;
            if (_dragonflyT >= 0)
            {
                _dragonflyT += dt / 6;
                if (_dragonflyT > 1) { _dragonflyT = -1; _dragonflyAt = _time + 30; }
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Season is not { } season) return;
        double w = ActualWidth, h = ActualHeight;
        switch (season)
        {
            case Services.Season.Spring: DrawShapes(dc, Petal); break;
            case Services.Season.Autumn: DrawFog(dc, w, h); DrawShapes(dc, Leaf); break;
            case Services.Season.Summer: DrawGlints(dc); DrawDragonfly(dc, w, h); break;
            case Services.Season.Winter: DrawFrost(dc, w, h); DrawSnow(dc); break;
            case Services.Season.NewYear: DrawSnow(dc); DrawGarland(dc, w); break;
        }
    }

    private void DrawShapes(DrawingContext dc, Geometry g)
    {
        foreach (var p in _p)
        {
            dc.PushTransform(new TranslateTransform(p.X, p.Y));
            dc.PushTransform(new RotateTransform(p.Rot));
            dc.PushTransform(new ScaleTransform(p.Size, p.Size));
            dc.PushOpacity(0.75);
            dc.DrawGeometry(p.Fill, null, g);
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
    }

    private void DrawSnow(DrawingContext dc)
    {
        foreach (var p in _p)
        {
            dc.PushOpacity(0.45 + p.Size / 8);
            dc.DrawEllipse(Snow, null, new Point(p.X, p.Y), p.Size, p.Size);
            dc.Pop();
        }
    }

    private void DrawGlints(DrawingContext dc)
    {
        foreach (var p in _p)
        {
            var a = Math.Max(0, Math.Sin(p.Phase));
            if (a < 0.05) continue;
            var s = p.Size * (0.6 + a);
            var star = new StreamGeometry();
            using (var c = star.Open())
            {
                c.BeginFigure(new Point(p.X, p.Y - s * 2), true, true);
                c.LineTo(new Point(p.X + s * 0.4, p.Y), true, false);
                c.LineTo(new Point(p.X, p.Y + s * 2), true, false);
                c.LineTo(new Point(p.X - s * 0.4, p.Y), true, false);
            }
            dc.PushOpacity(a * 0.6);
            dc.DrawGeometry(Snow, null, star);
            dc.DrawRectangle(Snow, null, new Rect(p.X - s * 1.6, p.Y - 0.5, s * 3.2, 1));
            dc.Pop();
        }
    }

    private void DrawDragonfly(DrawingContext dc, double w, double h)
    {
        if (_dragonflyT < 0) return;
        var x = -40 + (w + 80) * _dragonflyT;
        var y = h * 0.35 + Math.Sin(_dragonflyT * Math.PI * 4) * 40;
        var flap = Math.Sin(_time * 50) * 0.5 + 0.5;
        var body = Frozen("#0EA5E9")[0];
        var wing = Frozen("#BAE6FD")[0];
        dc.PushOpacity(0.85);
        dc.DrawRectangle(body, null, new Rect(x - 14, y - 1.5, 22, 3));
        dc.DrawEllipse(body, null, new Point(x + 9, y), 3, 3);
        dc.PushOpacity(0.5 + 0.3 * flap);
        foreach (var (dx, dir) in new[] { (2.0, -1.0), (2.0, 1.0), (-4.0, -1.0), (-4.0, 1.0) })
            dc.DrawEllipse(wing, null, new Point(x + dx, y + dir * (5 + 3 * flap)), 6, 2.5 + flap);
        dc.Pop();
        dc.Pop();
    }

    private static readonly Brush FogBrush = MakeFog();
    private static Brush MakeFog()
    {
        var b = new LinearGradientBrush(Color.FromArgb(0, 200, 190, 180), Color.FromArgb(70, 200, 190, 180), 90);
        b.Freeze();
        return b;
    }

    private void DrawFog(DrawingContext dc, double w, double h)
    {
        dc.PushOpacity(0.7 + 0.3 * Math.Sin(_time * 0.4));
        dc.DrawRectangle(FogBrush, null, new Rect(0, h * 0.78, w, h * 0.22));
        dc.Pop();
    }

    private static readonly Brush FrostEdge = MakeFrost();
    private static Brush MakeFrost()
    {
        var b = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 224, 242, 254), 0.82),
                new GradientStop(Color.FromArgb(38, 224, 242, 254), 1),
            },
        };
        b.Freeze();
        return b;
    }

    private static void DrawFrost(DrawingContext dc, double w, double h) =>
        dc.DrawRectangle(FrostEdge, null, new Rect(0, 0, w, h));

    /// <summary>Гирлянда по верхнему краю: провисающий провод и лампочки, мигают по очереди.</summary>
    private void DrawGarland(DrawingContext dc, double w)
    {
        const double span = 90, sag = 14, top = 2;
        for (double x = 0; x < w; x += span)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, top), false, false);
                c.QuadraticBezierTo(new Point(x + span / 2, top + sag * 2), new Point(x + span, top), true, false);
            }
            dc.DrawGeometry(null, Wire, g);
            for (var i = 1; i <= 4; i++)
            {
                var t = i / 5.0;
                var bx = x + span * t;
                var by = top + 2 * sag * 2 * t * (1 - t) + 4;
                var idx = (int)(x / span) * 4 + i;
                var on = Math.Sin(_time * 3 + idx * 1.3) > -0.2;
                dc.PushOpacity(on ? 1 : 0.25);
                dc.DrawEllipse(BulbFills[idx % BulbFills.Length], null, new Point(bx, by), 3.5, 4.5);
                dc.Pop();
            }
        }
    }
}

/// <summary>Новогодний фейерверк на всё окно: залпы искр с гравитацией. Щелчок — закрыть.</summary>
public sealed class FireworksView : FrameworkElement
{
    private struct Spark { public double X, Y, Vx, Vy, Life; public Brush Fill; }

    private readonly Random _rnd = new();
    private readonly List<Spark> _sparks = new();
    private DateTime _last;
    private double _nextBurst;
    private bool _running;

    private static readonly Brush[] Colors = new[] { "#EF4444", "#FACC15", "#22C55E", "#3B82F6", "#EC4899", "#F97316", "#FFFFFF" }
        .Select(h => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); b.Freeze(); return (Brush)b; }).ToArray();

    public FireworksView()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_running) { _last = DateTime.Now; _nextBurst = 0; CompositionTarget.Rendering += OnFrame; _running = true; }
            else if (!IsVisible && _running) { CompositionTarget.Rendering -= OnFrame; _running = false; _sparks.Clear(); }
        };
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        _nextBurst -= dt;
        if (_nextBurst <= 0)
        {
            Burst(_rnd.NextDouble() * ActualWidth * 0.8 + ActualWidth * 0.1, _rnd.NextDouble() * ActualHeight * 0.45 + ActualHeight * 0.1);
            _nextBurst = 0.35 + _rnd.NextDouble() * 0.5;
        }
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            s.Vy += 90 * dt;
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.Life -= dt * 0.7;
            if (s.Life <= 0) _sparks.RemoveAt(i); else _sparks[i] = s;
        }
        InvalidateVisual();
    }

    private void Burst(double x, double y)
    {
        var fill = Colors[_rnd.Next(Colors.Length)];
        var speed = 120 + _rnd.NextDouble() * 80;
        for (var i = 0; i < 60; i++)
        {
            var a = Math.PI * 2 * i / 60;
            var v = speed * (0.7 + _rnd.NextDouble() * 0.3);
            _sparks.Add(new Spark { X = x, Y = y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v, Life = 1, Fill = fill });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 5, 8, 20)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        foreach (var s in _sparks)
        {
            dc.PushOpacity(Math.Clamp(s.Life, 0, 1));
            dc.DrawEllipse(s.Fill, null, new Point(s.X, s.Y), 2.2, 2.2);
            dc.Pop();
        }
    }
}
