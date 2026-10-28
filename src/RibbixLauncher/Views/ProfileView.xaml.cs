using System.Windows.Controls;
using System.Windows.Input;
using RibbixLauncher.ViewModels;

namespace RibbixLauncher.Views;

public partial class ProfileView : UserControl
{
    public ProfileView() => InitializeComponent();

    /// <summary>Клик по правой половине карточки — дальше, по левой — назад, как в сторис.</summary>
    private void RecapPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: YearRecapViewModel vm } img) return;
        if (e.GetPosition(img).X < img.ActualWidth / 3) vm.PrevCommand.Execute(null);
        else vm.NextCommand.Execute(null);
    }
}
