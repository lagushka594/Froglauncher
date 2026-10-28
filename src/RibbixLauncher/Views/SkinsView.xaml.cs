using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RibbixLauncher.ViewModels;

namespace RibbixLauncher.Views;

public partial class SkinsView : UserControl
{
    public SkinsView() => InitializeComponent();

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SkinsViewModel vm && sender is FrameworkElement { DataContext: SkinItem item }) vm.Selected = item;
    }

    /// <summary>Двойной клик по скину — надеть сразу.</summary>
    private void Tile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SkinsViewModel vm && sender is FrameworkElement { DataContext: SkinItem item }) vm.WearCommand.Execute(item);
    }
}
