using DeskCanvas.WinUI.WidgetEditors.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// “未迁移 / 插件需适配 / 页面创建失败”的说明页（报告 §3 第三步 S3.2）。
/// <para>
/// 存在的意义是把“没有实现”明确地显示出来：打开一个未迁移的设置页时，用户看到的是
/// 这条说明，而不是旧 Avalonia 页面，也不是一个空白窗口。覆盖率统计（<c>--check-editors</c>）
/// 与本页展示的是同一份注册表状态，避免“全部命中”的假象。
/// </para>
/// </summary>
internal sealed class NotMigratedEditorPage : IWidgetEditorPage
{
    private readonly StackPanel root;

    public NotMigratedEditorPage(
        WidgetEditorContext context,
        WidgetEditorMigrationState state,
        string? detail = null,
        bool unavailable = false)
    {
        var isPlugin = state == WidgetEditorMigrationState.PluginNeedsAdapter;

        var title = unavailable
            ? WidgetEditorStrings.PageUnavailableTitle
            : isPlugin ? WidgetEditorStrings.NeedsAdapterTitle : WidgetEditorStrings.NotMigratedTitle;

        var body = unavailable
            ? WidgetEditorStrings.NotMigratedBody
            : isPlugin ? WidgetEditorStrings.NeedsAdapterBody : WidgetEditorStrings.NotMigratedBody;

        root = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16, 8, 16, 16),
        };

        root.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = unavailable ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
            Title = title,
            Message = body,
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            root.Children.Add(EditorUi.Caption(detail!));
        }

        root.Children.Add(EditorUi.Divider());
        root.Children.Add(EditorUi.Caption(WidgetEditorStrings.OriginalPageLabel));
        root.Children.Add(EditorUi.Caption(
            $"{context.Request.SettingsViewAssemblyName}:{context.Request.SettingsViewTypeName}"));
        root.Children.Add(EditorUi.Caption(
            $"{context.Request.AssemblyName}/{context.Request.SubType}"));
    }

    public UIElement View => root;

    public void Attach()
    {
        // 说明页没有数据订阅。
    }

    public void Detach()
    {
    }
}
