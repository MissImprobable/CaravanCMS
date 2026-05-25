using CaravanCMS.Client.Services;
using CaravanCMS.Client.ViewModels;
using CaravanCMS.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CaravanCMS.Client.Views;

public partial class CaravanDetailWindow : Window
{
    private readonly CaravanViewModel _vm;

    public CaravanDetailWindow(ApiClient api, CaravanSummaryDto summary)
    {
        InitializeComponent();
        _vm = new CaravanViewModel(api);
        DataContext = _vm;

        Title = $"CaravanCMS — {summary.Make} {summary.Model} ({summary.RegistrationNumber ?? summary.Vin ?? "No ID"})";

        Loaded += async (_, _) => await _vm.LoadAsync(summary.RegistrationNumber ?? string.Empty);
    }
}

/// <summary>Converts MIME type strings to representative emoji icons.</summary>
public class MimeTypeToIconConverter : IValueConverter
{
    public static readonly MimeTypeToIconConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "application/pdf" => "📄",
            "image/jpeg" or "image/png" or "image/tiff" or "image/bmp" => "🖼",
            var m when m?.StartsWith("image/") == true => "🖼",
            var m when m?.Contains("word") == true => "📝",
            var m when m?.Contains("excel") == true || m?.Contains("sheet") == true => "📊",
            _ => "📎"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts null/empty string to Collapsed, non-null to Visible.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Returns Visible when an integer is zero (for "no items" messages).</summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public static readonly ZeroToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
