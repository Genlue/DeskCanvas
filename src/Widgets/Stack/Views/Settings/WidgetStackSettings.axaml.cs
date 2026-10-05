using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StackWidgets.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace StackWidgets.Views.Settings;

public record AvailableWidgetOption(string AssemblyName, string ViewTypeName, string Title)
{
    public override string ToString() => Title;
}

public partial class WidgetStackSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private readonly IAssemblyProvider assemblyProvider;
    private WidgetStackModel model;
    private bool isInitializing = true;

    private static readonly List<AvailableWidgetOption> AvailableOptions =
    [
        new("Clock", "AnalogI", "时钟 (经典表盘 I)"),
        new("Clock", "Digital", "时钟 (数字时钟)"),
        new("Notes", "Note", "便签"),
        new("Reminders", "List", "待办清单"),
        new("Monitor", "SingleMetric", "系统监控 (轻量仪表)"),
        new("Monitor", "MultiDashboard", "系统监控 (全能看板)"),
        new("Weather", "Forecast", "天气预报"),
        new("Batteries", "BatteriesView", "电池电量"),
        new("Pomodoro", "PomodoroView", "番茄工作法"),
        new("Progress", "ProgressView", "进度刻度"),
        new("Search", "SearchView", "快捷搜索"),
        new("Tools", "ClipboardView", "剪贴板"),
        new("Tools", "TranslatorView", "翻译工具"),
        new("Folders", "Folder", "文件夹 (标准)"),
        new("Folders", "BigFolder", "大文件夹"),
        new("Picture", "PictureView", "相册照片"),
        new("Map", "MapView", "全景地图"),
        new("Calendar", "Date", "日历 (日期)"),
        new("Calendar", "Month", "日历 (月历)")
    ];

    public WidgetStackSettings() : this(null!, null!)
    {
    }

    public WidgetStackSettings(IWidgetLayoutProvider? widgetLayoutProvider, IAssemblyProvider? assemblyProvider = null)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        this.assemblyProvider = assemblyProvider ?? (DeskCanvas.App.Services?.GetService(typeof(IAssemblyProvider)) as IAssemblyProvider)!;
        model = widgetLayoutProvider?.Get().GetModel<WidgetStackModel>() ?? new WidgetStackModel();

        InitializeComponent();

        AvailableWidgetsCombo.ItemsSource = AvailableOptions;
        AvailableWidgetsCombo.SelectedIndex = 0;

        LoadFromModel();
        isInitializing = false;

        Loaded += (_, _) =>
        {
            if (VisualRoot is Window win)
            {
                StackDropCoordinator.ActiveTargetWindow = win;
                StackDropCoordinator.ActiveDropHandler = OnDesktopWidgetDropped;
            }
        };

        Unloaded += (_, _) =>
        {
            if (VisualRoot is Window win && StackDropCoordinator.ActiveTargetWindow == win)
            {
                StackDropCoordinator.ActiveTargetWindow = null;
                StackDropCoordinator.ActiveDropHandler = null;
            }
        };
    }

    private bool OnDesktopWidgetDropped(WidgetLayout layout)
    {
        try
        {
            if (layout.Type is "Stack" or "StackWidgets") return false;

            var opt = AvailableOptions.FirstOrDefault(o => o.AssemblyName == layout.Type && o.ViewTypeName == layout.SubType);
            string title = opt?.Title ?? ResolveDynamicTitle(layout);
            string? settingsJson = layout.Settings?.GetRawText();

            bool wasEmpty = model.Entries.Count == 0;

            var newEntry = new StackedWidgetEntry(layout.Type, layout.SubType, title, settingsJson, layout.ContentScale);
            var list = model.Entries.ToList();
            list.Add(newEntry);
            model = model with { Entries = list };

            SaveModel();

            if (wasEmpty)
            {
                var cur = widgetLayoutProvider.Get();
                widgetLayoutProvider.Save(cur with
                {
                    Width = layout.Width,
                    Height = layout.Height,
                    ContentScale = layout.ContentScale
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                RefreshList();
            });

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStackSettings] OnDesktopWidgetDropped error: {ex.Message}");
            return false;
        }
    }

    private string ResolveDynamicTitle(WidgetLayout layout)
    {
        try
        {
            var assembly = assemblyProvider?.LoadAssembly(layout.Type);
            var widgetInfo = assembly?
                .GetCustomAttributes<WidgetInfoAttribute>()
                .FirstOrDefault(a => a.ViewType.Name == layout.SubType);
            if (assembly != null && widgetInfo != null && !string.IsNullOrEmpty(widgetInfo.Title))
            {
                var locale = assemblyProvider?.GetLocaleResourceManager(assembly);
                var localized = locale?.GetString(widgetInfo.Title);
                return !string.IsNullOrEmpty(localized) ? localized : widgetInfo.Title;
            }
        }
        catch { }

        return $"{layout.Type} - {layout.SubType}";
    }

    private void LoadFromModel()
    {
        AllowWheelSwitch.IsChecked = model.AllowWheelSwitch;
        WheelOnlyOnDotsSwitch.IsChecked = model.WheelSwitchOnlyOnDots;
        AutoHideDotsSwitch.IsChecked = model.AutoHideDots;
        RefreshList();
    }

    private bool CheckHasSettings(StackedWidgetEntry entry)
    {
        try
        {
            var assembly = assemblyProvider?.LoadAssembly(entry.AssemblyName);
            var widgetInfo = assembly?
                .GetCustomAttributes<WidgetInfoAttribute>()
                .FirstOrDefault(a => a.ViewType.Name == entry.ViewTypeName);
            return widgetInfo?.EditModelViewType != null;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshList()
    {
        var items = new List<StackedWidgetSettingItem>();
        for (int i = 0; i < model.Entries.Count; i++)
        {
            var entry = model.Entries[i];
            items.Add(new StackedWidgetSettingItem
            {
                Entry = entry,
                Index = i,
                HasSettings = CheckHasSettings(entry)
            });
        }
        ItemsList.ItemsSource = null;
        ItemsList.ItemsSource = items;

        if (EmptyHintText != null)
        {
            EmptyHintText.IsVisible = model.Entries.Count == 0;
        }
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            AllowWheelSwitch = AllowWheelSwitch.IsChecked == true,
            WheelSwitchOnlyOnDots = WheelOnlyOnDotsSwitch.IsChecked == true,
            AutoHideDots = AutoHideDotsSwitch.IsChecked == true
        };
        SaveModel();
    }

    private void OnAddWidgetClicked(object? sender, RoutedEventArgs e)
    {
        if (AvailableWidgetsCombo.SelectedItem is AvailableWidgetOption opt)
        {
            var newEntry = new StackedWidgetEntry(opt.AssemblyName, opt.ViewTypeName, opt.Title);
            var list = model.Entries.ToList();
            list.Add(newEntry);
            model = model with { Entries = list };
            SaveModel();
            RefreshList();
        }
    }

    private async void OnEditChildClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StackedWidgetSettingItem item }) return;

        var entry = item.Entry;
        var assembly = assemblyProvider?.LoadAssembly(entry.AssemblyName);
        var widgetInfo = assembly?
            .GetCustomAttributes<WidgetInfoAttribute>()
            .FirstOrDefault(a => a.ViewType.Name == entry.ViewTypeName);

        if (widgetInfo?.EditModelViewType == null) return;

        var childLayoutProvider = new StackedChildLayoutProvider((updatedEntry, json) =>
        {
            var list = model.Entries.ToList();
            if (item.Index >= 0 && item.Index < list.Count)
            {
                list[item.Index] = list[item.Index] with { SettingsJson = json };
                model = model with { Entries = list };
                SaveModel();
            }
        }, entry, widgetLayoutProvider);

        var editControl = (UserControl)assemblyProvider!.Activate(widgetInfo.EditModelViewType, childLayoutProvider);
        var editWindow = new EditWidget(childLayoutProvider, editControl);

        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel != null)
        {
            await editWindow.ShowDialog(topLevel);
        }
        else
        {
            editWindow.Show();
        }
        RefreshList();
    }

    private void OnExtractToDesktopClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StackedWidgetSettingItem item }) return;

        try
        {
            var layoutProvider = DeskCanvas.App.Services?.GetService(typeof(ILayoutProvider)) as ILayoutProvider;
            if (layoutProvider != null)
            {
                var screens = layoutProvider.Get();
                var curLayout = widgetLayoutProvider.Get();
                var screenId = widgetLayoutProvider.ScreenId;
                var targetScreen = screens.FindById(screenId) ?? screens.Screens.FirstOrDefault();

                if (targetScreen != null)
                {
                    JsonElement? settings = null;
                    if (!string.IsNullOrEmpty(item.Entry.SettingsJson))
                    {
                        try { settings = JsonDocument.Parse(item.Entry.SettingsJson).RootElement.Clone(); } catch { }
                    }

                    var newWidget = new WidgetLayout(
                        item.Entry.AssemblyName,
                        item.Entry.ViewTypeName,
                        curLayout.X + 24,
                        curLayout.Y + 24,
                        curLayout.Width,
                        curLayout.Height,
                        settings,
                        item.Entry.ContentScale ?? curLayout.ContentScale
                    );

                    var updatedScreen = targetScreen with { Layout = [.. targetScreen.Layout, newWidget] };
                    layoutProvider.Save(screens.UpsertScreen(updatedScreen));
                }
            }

            var list = model.Entries.ToList();
            if (item.Index >= 0 && item.Index < list.Count)
            {
                list.RemoveAt(item.Index);
                int newSelected = model.SelectedIndex;
                if (newSelected >= list.Count) newSelected = Math.Max(0, list.Count - 1);
                model = model with { Entries = list, SelectedIndex = newSelected };
                SaveModel();
                RefreshList();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStackSettings] OnExtractToDesktopClicked error: {ex.Message}");
        }
    }

    private void OnMoveUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StackedWidgetSettingItem item)
        {
            int idx = item.Index;
            if (idx > 0)
            {
                var list = model.Entries.ToList();
                var entry = list[idx];
                list.RemoveAt(idx);
                list.Insert(idx - 1, entry);
                model = model with { Entries = list, SelectedIndex = Math.Min(model.SelectedIndex, list.Count - 1) };
                SaveModel();
                RefreshList();
            }
        }
    }

    private void OnMoveDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StackedWidgetSettingItem item)
        {
            int idx = item.Index;
            var list = model.Entries.ToList();
            if (idx >= 0 && idx < list.Count - 1)
            {
                var entry = list[idx];
                list.RemoveAt(idx);
                list.Insert(idx + 1, entry);
                model = model with { Entries = list, SelectedIndex = Math.Min(model.SelectedIndex, list.Count - 1) };
                SaveModel();
                RefreshList();
            }
        }
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StackedWidgetSettingItem item)
        {
            var list = model.Entries.ToList();
            int idx = item.Index;
            if (idx >= 0 && idx < list.Count)
            {
                list.RemoveAt(idx);
                int newSelected = model.SelectedIndex;
                if (newSelected >= list.Count) newSelected = Math.Max(0, list.Count - 1);
                model = model with { Entries = list, SelectedIndex = newSelected };
                SaveModel();
                RefreshList();
            }
        }
    }

    private void SaveModel()
    {
        try
        {
            var curLayout = widgetLayoutProvider.Get();
            var json = JsonSerializer.SerializeToElement(model);
            widgetLayoutProvider.Save(curLayout with { Settings = json });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetStackSettings] SaveModel error: {ex.Message}");
        }
    }
}
