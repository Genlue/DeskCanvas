using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// Host dialogs a workflow may need while it runs (input prompt, file pickers, result view).
/// Behind an interface so a check can drive a workflow that asks for input without a window,
/// and so every dialog reuses the same visual language instead of one-off windows.
/// </summary>
public interface IUserPromptService
{
    /// <summary>Ask for one value; null when the user cancelled (which ends the run).</summary>
    Task<RunValue?> AskAsync(string title, string? prompt, RunValue? defaultValue, DataType type,
        CancellationToken token);

    /// <summary>Pick one file; null when cancelled.</summary>
    string? PickFile(string title, string? filter);

    /// <summary>Pick one folder; null when cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Show a value in a scrollable, copyable view.</summary>
    Task ShowResultAsync(string title, RunValue value);
}

/// <summary>Production implementation: Avalonia flyout-free windows owned by the host surface.</summary>
public sealed class HostPromptService(Func<TopLevel?>? accessor) : IUserPromptService
{
    public Task<RunValue?> AskAsync(string title, string? prompt, RunValue? defaultValue, DataType type,
        CancellationToken token)
    {
        var owner = accessor?.Invoke() as Window;
        if (owner == null) throw new InvalidOperationException(Loc.Get("Shortcuts_Error_ConfirmUnavailable"));

        var completion = new TaskCompletionSource<RunValue?>();
        var box = new TextBox
        {
            Text = defaultValue?.ToText() ?? string.Empty,
            PlaceholderText = prompt,
            MinWidth = 280,
            AcceptsReturn = false,
        };

        var ok = new Button { Content = Loc.Get("Shortcuts_ConfirmRunOk"), IsDefault = true };
        var cancel = new Button { Content = Loc.Get("Shortcuts_Cancel"), IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        if (!string.IsNullOrWhiteSpace(prompt))
            panel.Children.Add(new TextBlock { Text = prompt, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        void Complete(RunValue? value)
        {
            if (completion.TrySetResult(value)) window.Close();
        }

        ok.Click += (_, _) =>
        {
            var text = box.Text ?? string.Empty;
            try
            {
                Complete(type switch
                {
                    DataType.Number => RunValue.FromNumber(double.Parse(text,
                        System.Globalization.CultureInfo.CurrentCulture)),
                    DataType.Boolean => RunValue.FromBoolean(bool.Parse(text)),
                    DataType.DateTime => RunValue.FromDate(DateTime.Parse(text,
                        System.Globalization.CultureInfo.CurrentCulture)),
                    _ => RunValue.FromText(text),
                });
            }
            catch
            {
                // A value that cannot be parsed keeps the dialog open: silently accepting it
                // would hand the workflow a value of the wrong type.
                box.Text = text;
            }
        };

        cancel.Click += (_, _) => Complete(null);
        window.Closed += (_, _) => completion.TrySetResult(null);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };

        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Complete(null)));

        window.ShowDialog(owner);
        box.Focus();
        return completion.Task;
    }

    public string? PickFile(string title, string? filter)
    {
        var owner = accessor?.Invoke() as Window;
        var handle = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return FileDialogs.PickFile(handle, title);
    }

    public string? PickFolder(string title)
    {
        var owner = accessor?.Invoke() as Window;
        var handle = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return FileDialogs.PickFolder(handle, title);
    }

    public Task ShowResultAsync(string title, RunValue value)
    {
        var owner = accessor?.Invoke() as Window;
        if (owner == null) throw new InvalidOperationException(Loc.Get("Shortcuts_Error_ConfirmUnavailable"));

        var text = value.Type == DataType.List
            ? string.Join(Environment.NewLine, value.AsList().Select(v => v.DisplayText))
            : value.ToText();

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 420,
            MinHeight = 220,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 12,
        };

        var close = new Button { Content = Loc.Get("Shortcuts_Close"), IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(close);

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var completion = new TaskCompletionSource();
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => completion.TrySetResult();
        window.ShowDialog(owner);
        return completion.Task;
    }
}
