using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace FrogLauncher.Installer
{
    /// <summary>
    /// Логика установки/удаления без UI (используется и окном, и тихим режимом --silent).
    /// Всё — в профиле пользователя и HKCU: права администратора не нужны.
    /// </summary>
    public sealed class SetupEngine
    {
        public const string AppName = "FrogLauncher";
        public const string ExeName = "FrogLauncher.exe";
        public const string UninstallExeName = "uninstall.exe";
        private const string PayloadResource = "FrogLauncher.Payload.zip";
        private const string UninstallerResource = "FrogLauncher.Uninstaller.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FrogLauncher";
        private const string GitHubUrl = "https://github.com/lagushka594/Froglauncher";

        public static string DefaultInstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string UserDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool HasPayload =>
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(PayloadResource);

        /// <summary>Каталог существующей установки (из реестра) — для обновления поверх.</summary>
        public static string ExistingInstallDir()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return key?.GetValue("InstallLocation") as string;
        }

        public static long PayloadSize()
        {
            using (var s = OpenPayload())
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                return zip.Entries.Sum(e => e.Length);
        }

        public static Process[] RunningLaunchers() =>
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));

        /// <summary>Установка. progress: 0..1 и текст этапа.</summary>
        public void Install(string dir, bool desktopShortcut, bool startMenuShortcut, Action<double, string> progress, CancellationToken ct)
        {
            if (!HasPayload)
                throw new InvalidOperationException("В установщике нет файлов лаунчера. Соберите его через build\\build.ps1.");

            Directory.CreateDirectory(dir);
            var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";

            progress(0, "Распаковываем лягушку…");
            using (var s = OpenPayload())
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = entry.Open())
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                            done += read;
                            progress(0.9 * done / total, "Копируем файлы " + entry.Name);
                        }
                    }
                }
            }

            progress(0.92, "Готовим деинсталлятор");
            var self = Process.GetCurrentProcess().MainModule.FileName;
            var uninstaller = Path.Combine(dir, UninstallExeName);
            using (var small = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallerResource))
            {
                if (small != null)
                {
                    // Лёгкий деинсталлятор (~1 МБ) вместо копии всего установщика.
                    using (var dst = File.Create(uninstaller)) small.CopyTo(dst);
                }
                else if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, uninstaller, overwrite: true);
                }
            }

            progress(0.95, "Создаём ярлыки");
            var exe = Path.Combine(dir, ExeName);
            if (desktopShortcut) CreateShortcut(DesktopShortcut, exe);
            if (startMenuShortcut) CreateShortcut(StartMenuShortcut, exe);

            progress(0.98, "Регистрируем froglauncher://");
            RegisterUrlScheme(exe);
            RegisterUninstall(dir, exe, uninstaller);

            progress(1, "Готово!");
        }

        public void Uninstall(string dir, bool removeUserData, Action<double, string> progress)
        {
            progress(0.1, "Удаляем ярлыки");
            TryDelete(DesktopShortcut);
            TryDelete(StartMenuShortcut);

            progress(0.3, "Чистим реестр");
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\froglauncher", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

            progress(0.5, "Удаляем файлы");
            var self = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    if (!string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }

            if (removeUserData && Directory.Exists(UserDataDir))
            {
                progress(0.8, "Удаляем миры и настройки");
                try { Directory.Delete(UserDataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            // Сам uninstall.exe и папку удалит cmd после нашего выхода.
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }

            progress(1, "Лягушка ускакала. До встречи!");
        }

        private static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        private static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        private static Stream OpenPayload() =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("Нет полезной нагрузки установщика");

        /// <summary>Ярлык через WScript.Shell (есть в любой Windows).</summary>
        private static void CreateShortcut(string lnkPath, string target)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = Path.GetDirectoryName(target);
                lnk.IconLocation = target + ",0";
                lnk.Description = "FrogLauncher — неофициальный лаунчер Minecraft";
                lnk.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RegisterUrlScheme(string exe)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\froglauncher"))
            {
                key.SetValue("", "URL:FrogLauncher Protocol");
                key.SetValue("URL Protocol", "");
                using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
            }
        }

        private static void RegisterUninstall(string dir, string exe, string uninstaller)
        {
            long sizeKb = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "FrogLauncher");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "FrogLauncher (неофициальный проект)");
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --silent");
                key.SetValue("URLInfoAbout", GitHubUrl);
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
