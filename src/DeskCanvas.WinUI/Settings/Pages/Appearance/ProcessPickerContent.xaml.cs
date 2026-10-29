using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Settings.Pages.Appearance;

/// <summary>
/// 失焦自动切换白名单的进程挑选内容：只列「有可见标题窗口」的进程（窗口白名单只对这类进程有意义），
/// 支持按进程名/窗口标题搜索、多选，关闭时由外观页读取 <see cref="SelectedProcessNames"/>。
/// <para>
/// 进程枚举复用 <see cref="FocusWatchService.EnumerateWindowedProcesses"/>，与旧
/// <c>ProcessPickerDialog</c> 及 Spotlight 黑名单是同一份来源，不在这里另写一套枚举。
/// </para>
/// <para>
/// 这是外观页的临时实现：S4.3 的统一 WinUI ProcessPicker 落地后，外观页应改为请求那个窗口，
/// 本文件随页面一起删除（不要在页面里留下第二套可独立演进的进程列表逻辑）。
/// </para>
/// </summary>
public sealed partial class ProcessPickerContent : UserControl
{
    private IReadOnlyList<ProcessPickerItem> all = [];

    public ProcessPickerContent()
    {
        InitializeComponent();

        SearchBox.PlaceholderText = Locale.ProcessPicker_Search;
        RefreshButton.Content = Locale.ProcessPicker_Refresh;
        EmptyText.Text = Locale.ProcessPicker_Empty;
        HintText.Text = Locale.ProcessPicker_Hint;

        Reload();
    }

    /// <summary>用户勾选的进程名（去重，保持大小写不敏感）。</summary>
    public IReadOnlyList<string> SelectedProcessNames =>
        ProcessList.SelectedItems is { Count: > 0 } selected
            ? selected.OfType<ProcessPickerItem>()
                .Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    private void Reload()
    {
        try
        {
            all = FocusWatchService.EnumerateWindowedProcesses()
                .Select(process => new ProcessPickerItem(process.Name, process.Title))
                .ToArray();
        }
        catch (Exception ex)
        {
            // 枚举失败不能让对话框炸掉：显示空列表，Refresh 可以重试。

            Debug.WriteLine($"[Appearance] 枚举进程失败：{ex}");
            all = [];
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();
        var items = string.IsNullOrEmpty(query)
            ? all
            : all
                .Where(item =>
                    item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();

        ProcessList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();
}

/// <summary>进程列表项（纯数据；<see cref="WindowedProcess"/> 是值类型，绑定到 DataTemplate 前先投影）。</summary>
public sealed record ProcessPickerItem(string Name, string? Title);
