using RibbixLauncher.Core;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace RibbixLauncher.Services;

/// <summary>
/// Значок в трее: лаунчер сворачивается сюда вместо закрытия. Меню: открыть, играть последней версией,
/// остановить игру, выход. Всплывающие уведомления — время в игре и новые версии Minecraft.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _play;
    private readonly WinForms.ToolStripMenuItem _stop;

    public event Action? OpenRequested;
    public event Action? PlayRequested;
    public event Action? StopRequested;
    public event Action? ExitRequested;

    /// <summary>true — пользователь выбрал «Выход»: окно закрывается по-настоящему.</summary>
    public bool IsExiting { get; private set; }

    public TrayService()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(L.T("Открыть Ribbix"), null, (_, _) => OpenRequested?.Invoke());
        _play = new WinForms.ToolStripMenuItem(L.T("▶ Играть (последняя версия)"), null, (_, _) => PlayRequested?.Invoke());
        _stop = new WinForms.ToolStripMenuItem(L.T("■ Остановить игру"), null, (_, _) => StopRequested?.Invoke()) { Enabled = false };
        menu.Items.Add(_play);
        menu.Items.Add(_stop);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход"), null, (_, _) => { IsExiting = true; ExitRequested?.Invoke(); });

        _icon = new WinForms.NotifyIcon
        {
            Text = "Ribbix 🐸",
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SetGameRunning(bool running)
    {
        _play.Enabled = !running;
        _stop.Enabled = running;
        _icon.Text = running ? L.T("Ribbix — игра запущена") : "Ribbix 🐸";
    }

    public void Notify(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info) =>
        Application.Current.Dispatcher.Invoke(() => _icon.ShowBalloonTip(5000, title, text, icon));

    public void MarkExiting() => IsExiting = true;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
