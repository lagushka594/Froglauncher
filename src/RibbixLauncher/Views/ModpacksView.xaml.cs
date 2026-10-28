using System.Windows;
using System.Windows.Controls;
using RibbixLauncher.ViewModels;

namespace RibbixLauncher.Views;

public partial class ModpacksView : UserControl
{
    public ModpacksView() => InitializeComponent();

    /// <summary>Переключатель раздела (Сборки / Моды / Ресурспаки / Шейдеры / Датапаки).</summary>
    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModpacksViewModel vm && sender is FrameworkElement { DataContext: ContentKind kind } && vm.Kind != kind) vm.Kind = kind;
    }
}
