using System.Windows;
using FrogLauncher.Views;

namespace FrogLauncher.Core;

public enum DialogKind { Info, Warning, Error, Success }

/// <summary>Красивые всплывающие окна в стиле лаунчера (вместо стандартных окон Windows).</summary>
public static class Dialogs
{
    public static void Error(string msg) => Show(Loc.T("dlg_error"), msg, DialogKind.Error, false);
    public static void Info(string msg) => Show("FrogLauncher", msg, DialogKind.Info, false);
    public static void Success(string msg) => Show("FrogLauncher", msg, DialogKind.Success, false);
    public static void Warn(string msg) => Show(Loc.T("dlg_warn"), msg, DialogKind.Warning, false);

    /// <summary>Вопрос «Да / Нет». danger = красная кнопка подтверждения (удаление и т.п.).</summary>
    public static bool Confirm(string msg, bool danger = false, string? title = null) =>
        Show(title ?? Loc.T("dlg_confirm"), msg, danger ? DialogKind.Warning : DialogKind.Info, true);

    private static bool Show(string title, string message, DialogKind kind, bool confirm)
    {
        var app = Application.Current;
        if (app == null) return false;
        bool result = false;
        void Run()
        {
            var w = new DialogWindow(title, message, kind, confirm);
            var owner = app.MainWindow;
            if (owner != null && owner.IsVisible && owner != w) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowDialog();
            result = w.Result;
        }
        if (app.Dispatcher.CheckAccess()) Run(); else app.Dispatcher.Invoke(Run);
        return result;
    }
}
