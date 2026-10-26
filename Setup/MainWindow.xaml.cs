using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace FrogLauncherSetup;

public partial class MainWindow : Window
{
    private int _page;                 // 0 приветствие, 1 папка, 2 установка, 3 готово, 4 ошибка
    private bool _installing;
    private string? _installedExe;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        DirBox.Text = InstallerCore.DefaultDir;
        if (!InstallerCore.HasPayload) { PayloadWarn.Visibility = Visibility.Visible; NextBtn.IsEnabled = false; }

        DragZone.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseBtn.Click += (_, _) => TryClose();
        CancelBtn.Click += (_, _) => TryClose();

        NextBtn.Click += (_, _) => Show(1);
        BackBtn.Click += (_, _) => Show(_page - 1);
        BrowseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку для установки" };
            if (dlg.ShowDialog() == true) DirBox.Text = dlg.FolderName;
        };
        InstallBtn.Click += async (_, _) => await InstallAsync();
        FinishBtn.Click += (_, _) =>
        {
            if (_page == 3 && RunChk.IsChecked == true && _installedExe != null)
            {
                try { Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true }); } catch { }
            }
            Close();
        };
        Show(0);
    }

    private void TryClose()
    {
        if (_installing)
        {
            if (!DialogWindow.Ask(this, "Прервать установку?", "Установка ещё не завершена. Остановить её и выйти?")) return;
            _cts?.Cancel();
        }
        Close();
    }

    private void Show(int page)
    {
        _page = page;
        PageWelcome.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageOptions.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageInstall.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageDone.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageError.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;

        NextBtn.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = page == 1 || page == 4 ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishBtn.Visibility = page >= 3 ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = page >= 3 ? Visibility.Collapsed : Visibility.Visible;
        if (page == 4) { FinishBtn.Content = "Закрыть"; BackBtn.Visibility = Visibility.Visible; }
        if (page == 3) FinishBtn.Content = "Готово";
    }

    private async Task InstallAsync()
    {
        OptErr.Text = "";
        string dir;
        try { dir = InstallerCore.Normalize(DirBox.Text); }
        catch { OptErr.Text = "Неверный путь к папке."; return; }

        Show(2);
        _installing = true;
        CancelBtn.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<(double percent, string text)>(p => { Bar.Value = p.percent; StatusText.Text = p.text; });
        try
        {
            _installedExe = await InstallerCore.InstallAsync(dir, DesktopChk.IsChecked == true, StartChk.IsChecked == true, progress, _cts.Token);
            Show(3);
        }
        catch (OperationCanceledException) { Close(); }
        catch (Exception ex)
        {
            ErrText.Text = ex.Message;
            Show(4);
        }
        finally { _installing = false; }
    }
}
