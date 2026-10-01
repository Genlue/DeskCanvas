using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// Small modal dialog with a message and confirm/cancel (or OK-only) buttons,
/// used by the backup import flow.
/// </summary>
public partial class ConfirmDialog : Window
{
    private bool confirmed;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void SetMessage(string? message) => MessageText.Text = message;

    private void SetButtons(string? confirmText, string? cancelText)
    {
        ConfirmButton.Content = confirmText;
        if (cancelText != null)
        {
            CancelButton.Content = cancelText;
            CancelButton.IsVisible = true;
        }
    }

    /// <summary>
    /// Show a confirm dialog; returns <c>true</c> when the confirm button was pressed.
    /// </summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string confirmText, string cancelText)
    {
        var dialog = new ConfirmDialog();
        dialog.SetMessage(message);
        dialog.SetButtons(confirmText, cancelText);
        await dialog.ShowDialog(owner);
        return dialog.confirmed;
    }

    /// <summary>Show an informational dialog with a single OK button.</summary>
    public static async Task InformAsync(Window owner, string message, string okText)
    {
        var dialog = new ConfirmDialog();
        dialog.SetMessage(message);
        dialog.SetButtons(okText, null);
        await dialog.ShowDialog(owner);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}