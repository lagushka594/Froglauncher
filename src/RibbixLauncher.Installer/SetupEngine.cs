using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace RibbixLauncher.Installer
{
    /// <summary>
    /// Логика установки/удаления без UI (используется и окном, и тихим режимом --silent).
    /// Всё — в профиле пользователя и HKCU: права администратора не нужны.
    /// </summary>
    public sealed class SetupEngine
    {
        public const string AppName = "RibbixLauncher";
        public const string ExeName = "RibbixLauncher.exe";
        public const string UninstallExeName = "uninstall.exe";
        private const string PayloadResource = "RibbixLauncher.Payload.zip";
        private const string UninstallerResource = "RibbixLauncher.Uninstaller.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RibbixLauncher";
        private const string GitHubUrl = "https://github.com/lagushka594/Ribbix_Studio";

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

        /// <summary>Лаунчер до переименования (до 2.6): его процессы, ярлыки, папка и запись в «Программах».</summary>
        public const string LegacyAppName = "FrogLauncher";
        private const string LegacyUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FrogLauncher";

        public static Process[] RunningLaunchers() =>
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
                .Concat(Process.GetProcessesByName(LegacyAppName)).ToArray();

        /// <summary>
        /// Обновление с FrogLauncher: удалить старую программу (не данные — их лаунчер перенесёт сам при первом запуске),
        /// ярлыки и запись в «Программах». Ссылки froglauncher:// дальше открывает новый exe.
        /// </summary>
        private static void RemoveLegacyInstall(string newDir)
        {
            string oldDir;
            using (var key = Registry.CurrentUser.OpenSubKey(LegacyUninstallKey))
                oldDir = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(oldDir))
                oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", LegacyAppName);
            if (Directory.Exists(oldDir) && !string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\'), Path.GetFullPath(newDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(oldDir, LegacyAppName + ".exe")))
            {
                try { Directory.Delete(oldDir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LegacyAppName + ".lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LegacyAppName + ".lnk"));
            Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallKey, throwOnMissingSubKey: false);
        }

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

            progress(0.97, "Убираем старый FrogLauncher");
            RemoveLegacyInstall(dir);

            progress(0.98, "Регистрируем ribbix://");
            RegisterUrlScheme(exe, "ribbix", "URL:Ribbix Protocol");
            // Старые ссылки (оплата, «Играть с другом») из FrogLauncher продолжают работать.
            RegisterUrlScheme(exe, "froglauncher", "URL:Ribbix Protocol (legacy)");
            RegisterUninstall(dir, exe, uninstaller);

            progress(1, "Готово!");
        }

        public void Uninstall(string dir, bool removeUserData, Action<double, string> progress)
        {
            progress(0.1, "Удаляем ярлыки");
            TryDelete(DesktopShortcut);
            TryDelete(StartMenuShortcut);

            progress(0.3, "Чистим реестр");
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ribbix", throwOnMissingSubKey: false);
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
                lnk.Description = "Ribbix — неофициальный лаунчер Minecraft";
                lnk.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RegisterUrlScheme(string exe, string scheme, string title)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + scheme))
            {
                key.SetValue("", title);
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
                key.SetValue("DisplayName", "Ribbix Launcher");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Ribbix Studio (неофициальный проект)");
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
