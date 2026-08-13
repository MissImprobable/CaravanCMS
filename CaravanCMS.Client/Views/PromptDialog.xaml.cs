using System.Windows;
using System.Windows.Input;

namespace CaravanCMS.Client.Views;

/// <summary>A minimal single-line input dialog — WPF has no built-in equivalent of
/// Microsoft.VisualBasic.Interaction.InputBox, so this fills that gap for the
/// "who should this go to?" prompts on the Package/Send-a-Copy buttons.</summary>
public partial class PromptDialog : Window
{
    public string? ResultText { get; private set; }

    public PromptDialog(string title, string message, string defaultValue = "", string? warning = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Text = defaultValue;
        InputBox.Focus();
        InputBox.SelectAll();

        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Shows the dialog and returns the entered text, or null if cancelled / left blank.</summary>
    public static string? Show(Window owner, string title, string message, string defaultValue = "", string? warning = null)
    {
        PromptDialog dialog = new(title, message, defaultValue, warning) { Owner = owner };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultText)
            ? dialog.ResultText!.Trim()
            : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }
}
