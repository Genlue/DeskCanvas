using System;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 页面键 → WinUI 页面。外壳只认这个接口，因此页面迁移批次与窗口外壳可以分别演进而互不改文件。
/// <para>
/// 实现（<see cref="SettingsPageFactory"/>）只做两件事：查 <see cref="SettingsPageRegistry"/>，
/// 未注册就回退占位页。页面本身在各自的文件里自注册，见 <see cref="SettingsPageRegistry"/>。
/// </para>
/// </summary>
public interface ISettingsPageFactory
{
    /// <summary>
    /// 为一个导航项创建页面。未迁移的页面必须返回占位页（<c>PagePlaceholder</c>），
    /// 不允许回退到旧的 Avalonia 设置页，也不允许抛异常让窗口炸掉（报告 §3 第二步）。

    /// </summary>
    ISettingsPage Create(SettingsPageContext context);
}
