using System;
using System.Linq;
using System.Windows;

namespace FrogLauncher.Installer
{
    /// <summary>
    /// Аргументы:
    ///   (без аргументов)          — мастер установки
    ///   --uninstall               — мастер удаления
    ///   --silent [--dir путь]     — тихая установка (CI, развёртывание)
    ///   --uninstall --silent [--purge] — тихое удаление (--purge: вместе с данными игры)
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
            bool uninstall = args.Contains("--uninstall"), silent = args.Contains("--silent");

            if (silent)
            {
                var code = RunSilent(e.Args, uninstall, args.Contains("--purge"));
                // Автообновление: после тихой установки снова запускаем лаунчер.
                if (code == 0 && !uninstall && args.Contains("--relaunch")) Relaunch(e.Args);
                Shutdown(code);
                return;
            }

            new MainWindow(uninstall).Show();
        }

        private static int RunSilent(string[] rawArgs, bool uninstall, bool purge)
        {
            try
            {
                var engine = new SetupEngine();
                var dirIndex = Array.FindIndex(rawArgs, a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase));
                var dir = dirIndex >= 0 && dirIndex + 1 < rawArgs.Length
                    ? rawArgs[dirIndex + 1]
                    : SetupEngine.ExistingInstallDir() ?? SetupEngine.DefaultInstallDir;

                WaitForLauncherExit();
                if (uninstall) engine.Uninstall(dir, purge, (_, __) => { });
                else engine.Install(dir, desktopShortcut: true, startMenuShortcut: true, (_, __) => { }, System.Threading.CancellationToken.None);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>Лаунчер сам закрывается перед обновлением — ждём до 20 с, затем завершаем принудительно.</summary>
        private static void WaitForLauncherExit()
        {
            foreach (var p in SetupEngine.RunningLaunchers())
            {
                try
                {
                    if (!p.WaitForExit(20000)) p.Kill();
                    p.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        private static void Relaunch(string[] rawArgs)
        {
            var dirIndex = Array.FindIndex(rawArgs, a => a.Equals("--dir", StringComparison.OrdinalIgnoreCase));
            var dir = dirIndex >= 0 && dirIndex + 1 < rawArgs.Length ? rawArgs[dirIndex + 1] : SetupEngine.ExistingInstallDir() ?? SetupEngine.DefaultInstallDir;
            var exe = System.IO.Path.Combine(dir, SetupEngine.ExeName);
            if (System.IO.File.Exists(exe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        }
    }
}
