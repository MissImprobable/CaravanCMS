using CaravanCMS.Client.Services;
using CaravanCMS.Client.ViewModels;
using CaravanCMS.Core;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CaravanCMS.Client.Views;

public partial class CaravanDetailWindow : Window
{
    private readonly CaravanViewModel _vm;
    private bool _documentsNewestFirst = true;

    public CaravanDetailWindow(ApiClient api, CaravanSummaryDto summary)
    {
        InitializeComponent();
        _vm = new CaravanViewModel(api);
        DataContext = _vm;

        Title = $"CaravanCMS — {summary.Make} {summary.Model} ({summary.RegistrationNumber ?? summary.Vin ?? "No ID"})";

        // Wired up only after the initial load finishes, not in XAML — SelectedDateChanged fires for
        // binding-driven changes too, so subscribing earlier would fire it once for every issue date
        // already on file and clobber real due dates the moment the window opens.
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(summary.RegistrationNumber ?? string.Empty);
            WofIssueDatePicker.SelectedDateChanged += (_, _) => ApplyDefaultExpiry(
                _vm.Caravan?.WofIssueDate, years: 1, setDueDate: d => _vm.Caravan!.WofDueDate = d);
            ElectricalWofIssueDatePicker.SelectedDateChanged += (_, _) => ApplyDefaultExpiry(
                _vm.Caravan?.ElectricalWofIssueDate, years: 4, setDueDate: d => _vm.Caravan!.ElectricalWofDueDate = d);
            SelfContainmentIssueDatePicker.SelectedDateChanged += (_, _) => ApplyDefaultExpiry(
                _vm.Caravan?.SelfContainmentIssueDate, years: 4, setDueDate: d => _vm.Caravan!.SelfContainmentDue = d);
        };
    }

    /// <summary>Pre-fills a due date as N years after the issue date whenever the issue date is set —
    /// WOF is a 1-year certificate, WOF Electrical and Self Containment are 4-year. Still just a default:
    /// the Due Date picker stays directly editable if a particular certificate's real expiry differs.</summary>
    private void ApplyDefaultExpiry(DateTime? issueDate, int years, Action<DateTime> setDueDate)
    {
        if (_vm.Caravan is null || issueDate is not DateTime issued) return;
        setDueDate(issued.AddYears(years));
        // CaravanDetailDto doesn't implement INotifyPropertyChanged, so mutating a field on it directly
        // doesn't push to the UI — force the Due Date picker to re-read by re-notifying on Caravan itself.
        _vm.RefreshCaravanDisplay();
    }

    /// <summary>Flips the Documents list between newest-first and oldest-first within each type group.
    /// Lives in code-behind (rather than the ViewModel) because it operates on the GroupedDocs
    /// CollectionViewSource, a WPF-specific view over the ViewModel's plain Documents collection.</summary>
    private void ToggleDocumentSort_Click(object sender, RoutedEventArgs e)
    {
        _documentsNewestFirst = !_documentsNewestFirst;

        CollectionViewSource cvs = (CollectionViewSource)Resources["GroupedDocs"];
        cvs.SortDescriptions.Clear();
        cvs.SortDescriptions.Add(new SortDescription(nameof(DocumentItemViewModel.DocumentType), ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(DocumentItemViewModel.Category), ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(
            nameof(DocumentItemViewModel.UploadedDate),
            _documentsNewestFirst ? ListSortDirection.Descending : ListSortDirection.Ascending));

        ((Button)sender).Content = _documentsNewestFirst ? "⬇ Newest first" : "⬆ Oldest first";
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

/// <summary>Labels a document's Category group ("2024 - Roger Kidd", the source import folder) so a PDF
/// and its accompanying photos from the same job read as one bundle instead of separate flat rows; falls
/// back to a friendly label for manually-added documents that were never imported from a dated folder.</summary>
public class CategoryGroupNameConverter : IValueConverter
{
    public static readonly CategoryGroupNameConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value?.ToString()) ? "Other documents" : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Returns Visible when an integer is non-zero (for toolbars that only make sense once there's something to act on).</summary>
public class NonZeroToVisibilityConverter : IValueConverter
{
    public static readonly NonZeroToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count != 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Returns Visible when the two bound values are equal (used to show only the expanded item in a list).</summary>
public class EqualityToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
