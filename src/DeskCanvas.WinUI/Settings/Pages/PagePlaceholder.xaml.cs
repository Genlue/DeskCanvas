using System;
using DeskCanvas.WinUI.Settings.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Settings.Pages;

/// <summary>
/// 尚未迁移页面的统一落点。明确告诉用户「该页面尚未迁移到 WinUI」，并显示页面键与涉及的组件包，
/// 便于下一轮迁移时直接对上报告里的页面表。
/// <para>
/// 它同时也是 **XAML 页面的生命周期参考实现**：<see cref="ISettingsPage"/> 的 Attach/Detach 转发给
/// <see cref="SettingsPageLifetime"/>（幂等 + Context 自动清空），下一批页面照这个骨架写即可。
/// 占位页现在没有订阅和计时器，但 Detach 仍然清空全部显示文本与持有引用——外壳的「隐藏即释放」
/// 契约不因为页面是空壳就打折，这样把占位页换成真实页面时，释放路径已经是被验证过的那一条。
/// </para>
/// <para>
/// TODO(报告 §3 第二步 / §3 第三步)：每迁移一个页面就用 <see cref="SettingsPageRegistry.Register"/>
/// 认领它的页面键，占位页自然不再出现；全部页面迁移完成后删除本文件及其资源键。
/// </para>
/// </summary>
public sealed partial class PagePlaceholder : UserControl, ISettingsPage
{
    private readonly SettingsPageLifetime lifetime = new();

    public PagePlaceholder()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    public UIElement View => this;

    /// <inheritdoc />
    public void Attach(SettingsPageContext context) => lifetime.Attach(context, OnAttachPage);

    /// <inheritdoc />
    public void Detach() => lifetime.Detach(OnDetachPage);

    private void OnAttachPage(SettingsPageContext context)
    {
        MigrationNotice.Title = SettingsShellStrings.PlaceholderTitle;
        MigrationNotice.Message = context.Title;
        BodyText.Text = SettingsShellStrings.PlaceholderBody;
        PageKeyText.Text = $"{SettingsShellStrings.PlaceholderPageKeyLabel}: {context.PageKey}";

        if (context.Assemblies.Count > 0)
        {
            var names = new string[context.Assemblies.Count];
            for (var i = 0; i < context.Assemblies.Count; i++)
            {
                names[i] = context.Assemblies[i].DisplayName;
            }

            PacksText.Text = $"{SettingsShellStrings.PlaceholderPacksLabel}: {string.Join(", ", names)}";
            PacksText.Visibility = Visibility.Visible;
        }
        else
        {
            PacksText.Text = string.Empty;
            PacksText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDetachPage()
    {
        // 占位页没有订阅可退，但仍要断开一切引用：设置窗口隐藏后页面实例会被外壳丢弃，
        // 这里再清一遍可以保证即使有人（测试/诊断）继续持有占位页，也不会间接拖住组件包元数据。

        MigrationNotice.Title = string.Empty;
        MigrationNotice.Message = string.Empty;
        BodyText.Text = string.Empty;
        PageKeyText.Text = string.Empty;
        PacksText.Text = string.Empty;
        PacksText.Visibility = Visibility.Collapsed;
    }
}
