using System.Windows;
using FrogLauncher.Core;
using FrogLauncher.Services;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Views;

/// <summary>Диалог «Новый инстанс»: название, версия Minecraft, модлоадер и его версия.</summary>
public partial class NewInstanceWindow : Window
{
    private readonly MainViewModel _vm;

    public string InstanceName { get; private set; } = "";
    public string McId { get; private set; } = "";
    public string Loader { get; private set; } = "Vanilla";
    public string? LoaderVersion { get; private set; }

    public NewInstanceWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        VersionBox.ItemsSource = vm.AllVersionIds;
        // по умолчанию — самая свежая версия из списка, которая является релизом, иначе первая
        VersionBox.SelectedIndex = 0;
        LoaderBox.ItemsSource = ModLoaderService.Loaders;
        LoaderBox.SelectedIndex = 0;

        VersionBox.SelectionChanged += async (_, _) => { UpdateName(); await LoadLoaderVersionsAsync(); };
        LoaderBox.SelectionChanged += async (_, _) => { UpdateName(); await LoadLoaderVersionsAsync(); };
        UpdateName();

        CancelBtn.Click += (_, _) => DialogResult = false;
        CreateBtn.Click += (_, _) =>
        {
            if (VersionBox.SelectedItem is not string mc) { Dialogs.Error(Loc.T("select_version")); return; }
            var loader = (string?)LoaderBox.SelectedItem ?? "Vanilla";
            if (loader != "Vanilla" && LoaderVerBox.SelectedItem is not string)
            {
                Dialogs.Error("Для выбранного модлоадера нет версий под эту версию Minecraft. Выберите другую версию.");
                return;
            }
            McId = mc; Loader = loader;
            LoaderVersion = loader == "Vanilla" ? null : (string)LoaderVerBox.SelectedItem;
            InstanceName = string.IsNullOrWhiteSpace(NameBox.Text) ? DefaultName() : NameBox.Text.Trim();
            DialogResult = true;
        };
    }

    private string DefaultName()
    {
        var mc = (string?)VersionBox.SelectedItem ?? "";
        var loader = (string?)LoaderBox.SelectedItem ?? "Vanilla";
        return loader == "Vanilla" ? $"Minecraft {mc}" : $"{mc} {loader}";
    }

    private string _autoName = "";
    private void UpdateName()
    {
        // подставляем имя, пока пользователь не вписал своё
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _autoName)
            NameBox.Text = _autoName = DefaultName();
    }

    private async Task LoadLoaderVersionsAsync()
    {
        var loader = (string?)LoaderBox.SelectedItem ?? "Vanilla";
        LoaderVerPanel.Visibility = loader == "Vanilla" ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = "";
        LoaderVerBox.ItemsSource = null;
        if (loader == "Vanilla" || VersionBox.SelectedItem is not string mc) return;
        try
        {
            HintText.Text = "Получаю версии загрузчика…";
            var list = await _vm.GetLoaderVersionsAsync(loader, mc);
            // пока ждали, выбор мог измениться
            if ((string?)LoaderBox.SelectedItem != loader || (string?)VersionBox.SelectedItem != mc) return;
            LoaderVerBox.ItemsSource = list;
            LoaderVerBox.SelectedIndex = list.Count > 0 ? 0 : -1;
            HintText.Text = list.Count == 0 ? $"{loader}: нет версий для Minecraft {mc}" : "";
        }
        catch (Exception ex)
        {
            Log.Warn("Версии загрузчика: " + ex.Message);
            HintText.Text = "Не удалось получить версии загрузчика: " + ex.Message;
        }
    }
}
