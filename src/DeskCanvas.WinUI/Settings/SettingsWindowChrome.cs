namespace DeskCanvas.WinUI.Settings;

/// <summary>设置窗口的标题栏模式（对应旧 <c>AppSettings.EffectiveTitleBarStyle</c>）。</summary>
public enum SettingsTitleBarMode
{
    /// <summary>系统原生标题栏与系统窗口按钮（本轮唯一已实现）。</summary>
    Native = 0,

    /// <summary>macOS 风格红绿灯自绘标题栏（下一步实现，报告 §4.1）。</summary>
    TrafficLights = 1,
}

/// <summary>
/// 设置窗口的背景材质（对应旧外观设置里的材质选项）。
/// <para>
/// 这里刻意只列出语义，不实现：WinUI 的透明画刷不等于顶层窗口逐像素透桌面，亚克力需要
/// <c>DesktopAcrylicController</c>/自定义 SystemBackdrop，液态玻璃需要复用原光学计算并解决
/// 采样原点不是 Avalonia Window 的问题（报告 §4.1 两处障碍）。在那些解决之前，窗口保持
/// 不透明表面——用一个半透明矩形冒充「已实现」是本次迁移明确禁止的。
/// </para>
/// </summary>
public enum SettingsWindowMaterial
{
    /// <summary>不透明表面（本轮唯一已实现）。</summary>
    Opaque = 0,

    /// <summary>系统亚克力（未实现）。</summary>
    Acrylic = 1,

    /// <summary>液态玻璃（未实现）。</summary>
    LiquidGlass = 2,
}

/// <summary>
/// 设置窗口的外观策略槽位：标题栏模式 + 背景材质。
/// 下一步接「红绿灯 + 三种材质」时，改动点集中在 <see cref="SettingsWindow.ApplyTitleBarMode"/>
/// 与这里，不需要动导航/页面/生命周期代码。
/// </summary>
public sealed class SettingsWindowChrome
{
    /// <summary>当前标题栏模式；只有 <see cref="SettingsTitleBarMode.Native"/> 真正生效。</summary>
    public SettingsTitleBarMode TitleBarMode { get; internal set; } = SettingsTitleBarMode.Native;

    /// <summary>当前背景材质；只有 <see cref="SettingsWindowMaterial.Opaque"/> 真正生效。</summary>
    public SettingsWindowMaterial Material { get; internal set; } = SettingsWindowMaterial.Opaque;

    /// <summary>当前组合是否已经真实实现（未实现的请求会被降级，见 <see cref="SettingsWindow.ApplyTitleBarMode"/>）。</summary>
    public bool IsFullySupported
        => TitleBarMode == SettingsTitleBarMode.Native && Material == SettingsWindowMaterial.Opaque;
}
