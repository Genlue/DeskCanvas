using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

public partial class InputDialog : Window
{
    private bool confirmed;
    private Func<string, string?>? validator;

    public InputDialog()
    {
        InitializeComponent();
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        CancelButton.Content = Locales.Locale.Cancel ?? (isZh ? "取消" : "Cancel");
        ConfirmButton.Content = Locales.Locale.Settings_Advanced_Backup_OkButton ?? (isZh ? "确定" : "OK");
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string prompt,
        string defaultText = "",
        Func<string, string?>? validator = null)
    {
        var dialog = new InputDialog();
        dialog.TitleText.Text = title;
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = defaultText;
        dialog.validator = validator;

        await dialog.ShowDialog(owner);
        return dialog.confirmed ? dialog.InputBox.Text?.Trim() : null;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (validator != null)
        {
            var err = validator(text);
            if (!string.IsNullOrEmpty(err))
            {
                ErrorText.Text = err;
                ErrorText.IsVisible = true;
                return;
            }
        }

        confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirm(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
        else
        {
            ErrorText.IsVisible = false;
        }
    }
}
