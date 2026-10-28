using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace RibbixLauncher.Installer
{
    public partial class MainWindow : Window
    {
        private readonly SetupEngine _engine = new SetupEngine();
        private readonly bool _uninstallMode;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _busy;
        private bool _installed;

        public MainWindow(bool uninstallMode)
        {
            InitializeComponent();
            _uninstallMode = uninstallMode;
            ((Storyboard)Resources["Spin"]).Begin(this, true);
            ((Storyboard)Resources["Float"]).Begin(this, true);

            if (uninstallMode)
            {
                Title = "Удаление Ribbix";
                Show(PageUninstall);
                return;
            }

            var existing = SetupEngine.ExistingInstallDir();
            DirBox.Text = existing ?? SetupEngine.DefaultInstallDir;
            if (existing != null) WelcomeText.Text += "\n\nRibbix уже установлен — установщик обновит его, миры и настройки сохранятся.";
            if (SetupEngine.HasPayload) SpaceText.Text = "Потребуется ≈ " + (SetupEngine.PayloadSize() / (1024 * 1024)) + " МБ (без игровых файлов).";
        }

        private void Show(FrameworkElement page)
        {
            foreach (var p in new FrameworkElement[] { PageWelcome, PageOptions, PageProgress, PageDone, PageUninstall })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        private void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Accept_Changed(object sender, RoutedEventArgs e) => NextButton.IsEnabled = AcceptBox.IsChecked == true;

        private void Next_Click(object sender, RoutedEventArgs e) => Show(PageOptions);

        private void Back_Click(object sender, RoutedEventArgs e) => Show(PageWelcome);

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка для Ribbix", SelectedPath = DirBox.Text })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    DirBox.Text = Path.Combine(dlg.SelectedPath, SetupEngine.AppName);
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var dir = DirBox.Text.Trim();
            if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                MessageBox.Show(this, "Укажите корректную папку установки.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!EnsureLauncherClosed()) return;

            bool desktop = DesktopBox.IsChecked == true, start = StartMenuBox.IsChecked == true;
            Show(PageProgress);
            _busy = true;
            try
            {
                await Task.Run(() => _engine.Install(dir, desktop, start, Report, _cts.Token));
                _installed = true;
                DoneTitle.Text = "Ribbix установлен!";
                DoneText.Text = "Квак! Ярлыки созданы. Игра бесплатна всегда — подписка лишь добавляет удобства лаунчера.";
                FinishButton.Tag = Path.Combine(dir, SetupEngine.ExeName);
            }
            catch (OperationCanceledException)
            {
                Fail("Установка отменена.");
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                _busy = false;
            }
            Show(PageDone);
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLauncherClosed()) return;
            var dir = SetupEngine.ExistingInstallDir() ?? AppDomain.CurrentDomain.BaseDirectory;
            var removeData = RemoveDataBox.IsChecked == true;
            Show(PageProgress);
            _busy = true;
            try
            {
                await Task.Run(() => _engine.Uninstall(dir, removeData, Report));
                DoneTitle.Text = "Ribbix удалён";
                DoneText.Text = removeData ? "Все данные удалены. Возвращайтесь — лягушка будет ждать 🐸" : "Миры и настройки остались в %AppData%\\RibbixLauncher.";
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                _busy = false;
            }
            Show(PageDone);
        }

        private void Fail(string message)
        {
            DoneIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon_error.png"));
            DoneTitle.Text = "Что-то пошло не так";
            DoneText.Text = message;
        }

        private void Report(double value, string text) => Dispatcher.BeginInvoke(new Action(() =>
        {
            Fill.Width = Math.Max(0, (Fill.Parent as FrameworkElement).ActualWidth - 8) * Math.Max(0, Math.Min(1, value));
            StatusText.Text = text + "  " + (int)(value * 100) + "%";
        }));

        private bool EnsureLauncherClosed()
        {
            var running = SetupEngine.RunningLaunchers();
            if (running.Length == 0) return true;
            var r = MessageBox.Show(this, "Ribbix запущен. Закрыть его, чтобы продолжить?", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return false;
            foreach (var p in running)
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); } catch (InvalidOperationException) { }
            }
            return true;
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (_installed && LaunchBox.IsChecked == true && FinishButton.Tag is string exe && File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                if (_uninstallMode) return; // удаление не прерываем на середине
                _cts.Cancel();
                return;
            }
            Close();
        }
    }
}
