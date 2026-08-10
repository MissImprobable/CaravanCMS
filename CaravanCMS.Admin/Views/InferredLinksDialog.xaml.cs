using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using System.Windows;

namespace CaravanCMS.Admin.Views;

public partial class InferredLinksDialog : Window
{
    public InferredLinksDialog(ApiClient api)
    {
        InitializeComponent();
        DataContext = new InferredLinksViewModel(api);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
