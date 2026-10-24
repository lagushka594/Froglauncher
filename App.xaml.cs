using System.Diagnostics;
using System.Windows;
using FrogLauncher.Core;
using FrogLauncher.Views;

namespace FrogLauncher;

public partial class App : Application
{
    /// <summary>Id версии из ярлыка (FrogLauncher.exe --launch ID): запустить её сразу после старта.</summary>
    public static string? LaunchInstanceId { get; set; }

    /// <summary>Выходим намеренно (например, закрыли лаунчер на время игры) — не спрашивать подтверждение.</summary>
    public static bool Quitting { get; set; }

    private static bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureAll();

        // Удаление программы (из «Приложения и возможности»)
        if (e.Args.Contains("--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Uninstall.Run();
            Shutdown();
            return;
        }

        // Режим «помощник»: лаунчер был закрыт на время игры — ждём её завершения и открываем лаунчер снова
        if (e.Args.Length >= 2 && e.Args[0] == "--relaunch" && int.TryParse(e.Args[1], out var pid))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Task.Run(() =>
            {
                try { Process.GetProcessById(pid).WaitForExit(); } catch { /* игра уже закрыта */ }
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch { }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--launch") LaunchInstanceId = e.Args[i + 1];

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Необработанная ошибка UI", ex.Exception);
            ex.Handled = true;
            if (_errorShown) return;           // не заваливаем пользователя десятками окон
            _errorShown = true;
            try { Dialogs.Error("Непредвиденная ошибка: " + ex.Exception.Message + "\nПодробности: " + AppPaths.Logs); }
            finally { _errorShown = false; }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task", ex.Exception); ex.SetObserved(); };

        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>Смена акцентной темы на лету.</summary>
    public static void ApplyTheme(string name)
    {
        var (a, d) = name switch
        {
            "Cyan" => ("#35D6FF", "#126A85"),
            "Purple" => ("#B58CFF", "#5B3AA3"),
            _ => ("#4CE04B", "#1F7A2B")
        };
        var conv = new System.Windows.Media.BrushConverter();
        Current.Resources["AccentBrush"] = (System.Windows.Media.Brush)conv.ConvertFromString(a)!;
        Current.Resources["AccentDarkBrush"] = (System.Windows.Media.Brush)conv.ConvertFromString(d)!;
    }
}
