using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using System.Windows;

namespace CaravanCMS.Admin.Views;

public partial class DocumentReviewDialog : Window
{
    private readonly DocumentReviewViewModel _vm;

    public DocumentReviewDialog(ApiClient api, PendingReviewStore reviewStore, string rootPath)
    {
        InitializeComponent();
        _vm = new DocumentReviewViewModel(api, reviewStore, rootPath);
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
