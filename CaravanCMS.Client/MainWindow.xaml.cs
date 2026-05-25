using CaravanCMS.Client.Services;
using CaravanCMS.Client.ViewModels;
using CaravanCMS.Client.Views;
using CaravanCMS.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace CaravanCMS.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ApiClient _api;

    public MainWindow()
    {
        InitializeComponent();

        ClientSettings settings = App.Settings;
        _api = new ApiClient(settings.ApiBaseUrl, settings.ApiKey);
        _vm = new MainViewModel(_api);
        DataContext = _vm;

        _vm.CaravanLookupSuccess += caravan =>
        {
            CaravanDetailWindow detail = new(_api, caravan);
            detail.Owner = this;
            detail.Show();
        };

        Loaded += (_, _) => RegoBox.Focus();
    }

    private void RegoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
            _vm.LookupRegoCommand.Execute(null);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
            _vm.SearchCommand.Execute(null);
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is CaravanSummaryDto selected)
        {
            CaravanDetailWindow detail = new(_api, selected);
            detail.Owner = this;
            detail.Show();
        }
    }
}

/// <summary>Inverts BooleanToVisibilityConverter — Collapsed when true, Visible when false.</summary>
public class InverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
