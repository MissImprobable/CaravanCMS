using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CaravanCMS.Admin.Views;

public partial class CustomerConversationsDialog : Window
{
    public CustomerConversationsDialog(ApiClient api)
    {
        InitializeComponent();
        DataContext = new CustomerConversationsViewModel(api);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Converts null to Collapsed, non-null to Visible.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
