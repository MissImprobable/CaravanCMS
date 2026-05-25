using CaravanCMS.Admin.Services;
using CaravanCMS.Admin.ViewModels;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CaravanCMS.Admin.Views;

public partial class ImportDialog : Window
{
    public ImportDialog(ApiClient api)
    {
        InitializeComponent();
        DataContext = new ImportViewModel(api);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Returns a success/error background color based on a bool value.</summary>
public class BoolToSuccessColorConverter : IValueConverter
{
    public static readonly BoolToSuccessColorConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? Color.FromRgb(220, 240, 225)   // soft green
            : Color.FromRgb(250, 220, 220);  // soft red

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Converts a collection count to Visibility — Visible when count > 0.</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Joins an IEnumerable&lt;string&gt; into a newline-separated string for selectable display.</summary>
public class StringJoinConverter : IValueConverter
{
    public static readonly StringJoinConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(Environment.NewLine, items) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Inverts a bool — used to disable buttons while loading.</summary>
public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}
