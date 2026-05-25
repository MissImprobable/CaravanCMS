using System.Windows;
using System.Windows.Controls;

namespace CaravanCMS.Client.Views;

/// <summary>A simple label+value row for the vehicle info panel.</summary>
public partial class InfoRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(InfoRow),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(object), typeof(InfoRow),
            new PropertyMetadata(null, OnValueChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public InfoRow()
    {
        InitializeComponent();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoRow row)
            row.LabelText.Text = e.NewValue?.ToString() ?? string.Empty;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoRow row)
            row.ValueText.Text = e.NewValue?.ToString() ?? "—";
    }
}
