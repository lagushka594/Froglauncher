using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrogLauncher.Core;

namespace FrogLauncher.Views;

/// <summary>
/// Пиксельная кнопка из атласа с вшитой русской подписью. По-русски — исходная картинка как есть;
/// на других языках — вариант *_blank.png (подпись закрашена, tools/Localize/blank_buttons.py)
/// и переведённый текст поверх, в той же области и того же цвета. Длинные слова ужимаются (Viewbox).
/// </summary>
public sealed class ArtLabel : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(ArtLabel), new PropertyMetadata(null, (d, _) => ((ArtLabel)d).Build()));

    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Подпись по-русски (ключ словаря).</summary>
    public string Label { get; set; } = "";
    public Color LabelColor { get; set; } = Colors.White;
    /// <summary>Область подписи в долях картинки: «лево,верх,ширина,высота».</summary>
    public Rect LabelArea { get; set; } = new(0.15, 0.6, 0.7, 0.26);

    public ArtLabel() => Loaded += (_, _) => Build();

    private void Build()
    {
        if (Source is null) return;
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        var translated = L.Language != "ru";
        var path = translated ? Source.Replace(".png", "_blank.png") : Source;
        var image = new Image { Source = new BitmapImage(new Uri("pack://application:,,," + path)) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        SetRowSpan(image, 3);
        SetColumnSpan(image, 3);
        Children.Add(image);
        if (!translated || Label.Length == 0) return;

        var a = LabelArea;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(a.X, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(a.Width, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - a.X - a.Width), GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(a.Y, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(a.Height, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.001, 1 - a.Y - a.Height), GridUnitType.Star) });

        var text = new Viewbox
        {
            StretchDirection = StretchDirection.DownOnly,
            Child = new TextBlock
            {
                Text = L.T(Label), FontWeight = FontWeights.Bold, FontSize = 100,
                Foreground = new SolidColorBrush(LabelColor),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.8 },
            },
        };
        // Высота строки задаёт размер текста; ширина ограничивает длинные слова.
        text.Stretch = Stretch.Uniform;
        SetRow(text, 1);
        SetColumn(text, 1);
        Children.Add(text);
    }
}
