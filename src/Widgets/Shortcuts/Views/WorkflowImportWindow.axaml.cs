using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Shortcuts.Locales;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.Views;

/// <summary>
/// Paste-or-pick-a-file dialog for AI-authored workflow JSON. Stays open on validation errors
/// and shows them verbatim: the user's next move is usually "paste these lines back to the
/// model", so the messages must name the offending type/port, not just say "invalid".
/// </summary>
public partial class WorkflowImportWindow : Window
{
    public WorkflowImportResult? Result { get; private set; }

    public WorkflowImportWindow()
    {
        InitializeComponent();

        // 本地化文案（key 暂未加入 resx 时回落到英文，避免界面上显示原始 key）。
        var relayoutText = Loc.Get("Shortcuts_Import_Relayout");
        RelayoutCheck.Content = relayoutText == "Shortcuts_Import_Relayout"
            ? "Re-layout (ignore original coordinates)"
            : relayoutText;

        Loaded += (_, _) =>
        {
            JsonBox.Focus();
            LoadFromClipboardIfEmpty();
        };
    }

    /// <summary>Prefill from the clipboard when it already holds JSON (the common AI flow).</summary>
    private async void LoadFromClipboardIfEmpty()
    {
        try
        {
            var clipboard = Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.TryGetTextAsync();
            // The model reply may carry a safety notice or fenced code block around the JSON;
            // import extracts it, so prefill on any plausible reply instead of requiring "{".
            if (string.IsNullOrWhiteSpace(text) || !text.Contains('{')) return;
            if (!string.IsNullOrWhiteSpace(JsonBox.Text)) return;
            JsonBox.Text = text;
        }
        catch
        {
            // Clipboard access is best-effort.
        }
    }

    private void OnLoadFileClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ownerHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var path = FileDialogs.PickFile(ownerHandle, Loc.Get("Shortcuts_Import_FromFile"));
            if (string.IsNullOrEmpty(path)) return;

            JsonBox.Text = File.ReadAllText(path);
            SetStatus(null);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        // 勾选"重新排版"时忽略 JSON 里的坐标，统一走分层布局引擎。
        var parsed = WorkflowImport.Parse(
            WorkflowImport.ExtractJson(JsonBox.Text ?? string.Empty),
            relayout: RelayoutCheck.IsChecked == true);
        if (!parsed.Success)
        {
            SetStatus(string.Join("\n", parsed.Errors), isError: true);
            return;
        }

        if (parsed.Warnings.Count > 0)
        {
            // Warnings are informational (auto layout / auto-chained nodes); import proceeds.
            SetStatus(string.Join("\n", parsed.Warnings), isError: false);
        }

        Result = parsed;
        Close();
    }

    private void SetStatus(string? text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
        StatusText.Foreground = isError
            ? new SolidColorBrush(Color.Parse("#A32D2D"))
            : new SolidColorBrush(Color.Parse("#3B6D11"));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnImportClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Show the dialog and return the parsed result (null when cancelled).</summary>
    public static async Task<WorkflowImportResult?> PromptAsync(Window owner)
    {
        var window = new WorkflowImportWindow();
        await window.ShowDialog(owner);
        return window.Result;
    }
}
