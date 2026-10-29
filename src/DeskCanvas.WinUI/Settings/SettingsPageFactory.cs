using System;
using DeskCanvas.WinUI.Settings.Pages;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 默认页面工厂：先问 <see cref="SettingsPageRegistry"/>，没有认领者就返回占位页。
/// <para>
/// 未迁移的页面在这里统一收敛到 <see cref="PagePlaceholder"/>，而不是回退去打开旧的
/// Avalonia 设置页：旧页依赖 Avalonia 视觉树和它自己的 ViewModel，混进 WinUI 内容区既跑不起来，
/// 也会让「已经迁移」这一判断失真（报告 §3 第二步）。
/// </para>
/// </summary>
public sealed class SettingsPageFactory : ISettingsPageFactory
{
    public SettingsPageFactory(IServiceProvider services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>原宿主 DI 容器；页面注册项若想自己解析依赖可以从这里取。</summary>
    public IServiceProvider Services { get; }

    public ISettingsPage Create(SettingsPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SettingsPageRegistry.TryCreate(context, out var page))
        {
            return page;
        }

        // TODO(报告 §3 第二步系统页表 + §3 第三步 37 个组件设置页表)：页面迁移者用
        // SettingsPageRegistry.Register / RegisterPrefix 认领页面键；在认领之前这里保持占位页。

        return new PagePlaceholder();
    }
}
