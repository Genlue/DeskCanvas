using System;
using System.Collections.Generic;
using System.Linq;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ClockLocale = global::Clock.Locales.Locale;

namespace DeskCanvas.WinUI.WidgetEditors.ClockEditors;

/// <summary>
/// Clock 四个设置页（Analog/Digital/Flip/Frameless）的公共骨架，也是后。33 页的模板。/// <list type="bullet">
/// <item><description>页面只从 <see cref="WidgetEditorContext"/> 拿组件自己的 <c>IWidgetLayoutProvider</c>
/// 。DI 服务，不 new 窗口/服务。/description></item>
/// <item><description>保存语义沿用组件 ViewModel：开。下拉选中即保存；滑块与颜色输入只。/// “完成编辑”（<c>ValueCommitted</c>）时写入一次；构造页面只读模型，不写盘。/description></item>
/// <item><description>保存抛异。。<see cref="WidgetEditorContext.ReportSaveFailure"/>，窗口顶。/// InfoBar 显示错误；不吞掉失败。/description></item>
/// </list>
/// </summary>
internal abstract class ClockEditorPageBase : IWidgetEditorPage
{
    protected ClockEditorPageBase(WidgetEditorContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        // 。下留白：滚动条与最后一行之间不贴边（旧页面同样留了 10px 右边距）。
        Root = new StackPanel
        {
            Spacing = 2,
            Padding = new Thickness(0, 0, 12, 8),
        };
    }

    protected WidgetEditorContext Context { get; }

    protected StackPanel Root { get; }

    public UIElement View => Root;

    public virtual void Attach()
    {
    }

    public virtual void Detach()
    {
    }

    /// <summary>执行一次模型写入；异常显示在窗口顶部，不向 UI 事件处理器抛。/summary>
    protected void Save(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            Context.ReportSaveFailure(ex);
        }
    }

    protected void Add(UIElement element) => Root.Children.Add(element);

    /// <summary>
    /// 去重的系统时区列表（ViewModel 。TimeZones 可能把当前时区追加到系统列表末尾。
    /// 落在系统列表里时就会出现重复项）。
    /// </summary>
    protected static TimeZoneInfo[] DistinctZones(IEnumerable<TimeZoneInfo> zones)
        => zones.GroupBy(zone => zone.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();

    /// <summary>时区行（旧页面统一的“本机时区”开。+ 时区下拉）。/summary>
    protected SettingRow TimeZoneRow(
        TimeZoneInfo[] zones,
        TimeZoneInfo selected,
        Action<TimeZoneInfo> onSelected,
        bool visible)
    {
        var combo = EditorUi.Combo(
            zones,
            zone => zone.DisplayName,
            selected,
            zone => Save(() => onSelected(zone)),
            matches: (left, right) => string.Equals(left.Id, right.Id, StringComparison.Ordinal),
            minWidth: 195);
        AutomationProperties.SetName(combo, ClockLocale.Clock_TimeZone);

        return EditorUi.Row(ClockLocale.Clock_TimeZone, null, combo, visible, editorMinWidth: 195);
    }

    /// <summary>“显示秒。/ 显示日期”这类简单开关行。/summary>
    protected SettingRow ToggleRow(string title, bool value, Action<bool> onChanged, bool visible = true,
        string? subtitle = null)
    {
        var toggle = EditorUi.Toggle(value, onChanged);
        // 行标题是独立。TextBlock，不自动成为开关的无障碍名称；这里显式给开关一个名字。
        AutomationProperties.SetName(toggle, title);
        return EditorUi.Row(title, subtitle, toggle, visible);
    }
}
