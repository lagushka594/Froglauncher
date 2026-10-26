using System.Windows;

namespace FrogLauncherSetup;

/// <summary>Красивый диалог вместо стандартного MessageBox.</summary>
public partial class DialogWindow : Window
{
    public bool Result { get; private set; }

    public DialogWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title; MessageText.Text = message;
        YesBtn.Click += (_, _) => { Result = true; Close(); };
        NoBtn.Click += (_, _) => { Result = false; Close(); };
    }

    public static bool Ask(Window owner, string title, string message)
    {
        var w = new DialogWindow(title, message) { Owner = owner };
        w.ShowDialog();
        return w.Result;
    }
}
