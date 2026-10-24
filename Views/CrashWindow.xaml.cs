using System.Diagnostics;
using System.Windows;

namespace FrogLauncher.Views;

public partial class CrashWindow : Window
{
    public CrashWindow(string lastLines, string? mclogsUrl)
    {
        InitializeComponent();
        LogBox.Text = lastLines;
        LinkBtn.Visibility = string.IsNullOrEmpty(mclogsUrl) ? Visibility.Collapsed : Visibility.Visible;
        LinkBtn.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(mclogsUrl!) { UseShellExecute = true }); } catch { } };
        CopyBtn.Click += (_, _) => { try { Clipboard.SetText(lastLines); } catch { } };
        CloseBtn.Click += (_, _) => Close();
    }
}
