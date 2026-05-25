using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using Microsoft.Win32;
using System.Windows;

namespace CaravanCMS.Admin.Views;

public partial class ScanFilesDialog : Window
{
    private readonly ScanFilesViewModel _vm;

    public ScanFilesDialog(ApiClient api)
    {
        InitializeComponent();
        _vm = new ScanFilesViewModel(api);
        DataContext = _vm;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select folder to scan for caravan documents"
        };

        if (dialog.ShowDialog() == true)
            _vm.CustomFolderPath = dialog.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
