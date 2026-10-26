using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FrogLauncher.Core;

namespace FrogLauncher.Views;

public partial class DialogWindow : Window
{
    public bool Result { get; private set; }

    public DialogWindow(string title, string message, DialogKind kind, bool confirm)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        var art = kind switch
        {
            DialogKind.Error => "panel_error", DialogKind.Warning => "panel_warning",
            DialogKind.Success => "panel_success", _ => "panel_info"
        };
        Frame.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{art}.png"));

        OkBtn.Content = confirm ? Loc.T("dlg_yes") : Loc.T("dlg_ok");
        NoBtn.Content = Loc.T("dlg_no");
        NoBtn.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        if (confirm && kind == DialogKind.Warning) OkBtn.Style = (Style)FindResource("DangerButton");

        OkBtn.Click += (_, _) => { Result = true; Close(); };
        NoBtn.Click += (_, _) => { Result = false; Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Result = false; Close(); } };
    }
}
