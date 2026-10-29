using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Services;

namespace DeskCanvas.Views;

/// <summary>
/// Process picker of the 失焦自动切换 allowlist: every running process that owns a visible,
/// titled top-level window (searchable by process name or window title), multi-select, closes
/// with the chosen process names.
/// </summary>
public partial class ProcessPickerDialog : HostDialogWindow
{
    private IReadOnlyList<WindowedProcess> all = [];

    public ProcessPickerDialog()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        all = FocusWatchService.EnumerateWindowedProcesses();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();
        var items = string.IsNullOrEmpty(query)
            ? all
            : all.Where(process =>
                process.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (process.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        ProcessList.ItemsSource = items;
        EmptyHint.IsVisible = items.Count == 0;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var selected = ProcessList.SelectedItems?
            .OfType<WindowedProcess>()
            .Select(process => process.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        Close(selected);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(Array.Empty<string>());
}
