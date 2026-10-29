using System;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 设置窗口的表面接口：谁实现设置窗口，谁就实现它；<c>WinUiSoftwareUi</c> 只认这个接口，
/// 因此引导代码（S1）与设置窗口（S2）可以并行开发、互不引用具体类型。
/// </summary>
public interface ISettingsSurface
{
    /// <summary>显示并激活设置窗口，落到 <paramref name="pageKey"/>（null = 恢复上次页面）。</summary>
    void Show(string? pageKey = null);

    /// <summary>无参唤出（二次启动接管、托盘点击）。</summary>
    void Activate();

    /// <summary>隐藏窗口并卸下当前页内容（关闭按钮走这里，不等于退出）。</summary>
    void Hide();

    bool IsOpen { get; }

    /// <summary>
    /// 退出流程专用：解除关闭拦截并真正销毁窗口（设置窗口平时「关闭」只是隐藏）。

    /// 退出协调器在关闭组件窗口之前调用它；实现方必须可重复调用且不抛。

    /// </summary>
    void CloseForShutdown();
}

/// <summary>
/// 设置窗口的注册点。设置窗口实现方在自己的程序集里通过 <c>[ModuleInitializer]</c>
/// 往 <see cref="Factory"/> 注册构造方式，引导代码在首个 <c>OpenSettings</c> 时惰性创建。
/// 这样任何一个执行者都不需要改另一个执行者的文件。
/// </summary>
public static class SettingsSurfaceRegistry
{
    public static Func<IServiceProvider, ISettingsSurface>? Factory { get; set; }
}