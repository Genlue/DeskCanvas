namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// What happens to the widgets while every attached screen is covered by a fullscreen or
/// maximized application (高级设置 → 最大化或全屏时). The desktop count as covered is the
/// <c>FullscreenWatcherService</c> decision — every screen must be covered, a maximized
/// window on each monitor included, so widgets at the bottom of the z-order cannot be seen.
/// </summary>
public enum FullscreenBehavior
{
    /// <summary>
    /// 隐藏: hide the widget windows and release what the covered desktop cannot see — the
    /// shared timers pause, per-widget material caches and the desktop capture are given up,
    /// memory is trimmed. The historical default and the most aggressive option.
    /// </summary>
    Hide = 0,

    /// <summary>
    /// 暂停: keep the widgets visible with their last frame but freeze them — the shared
    /// timers stop and the hosted contents gate their own ticks and data polling. Nothing
    /// is hidden and no cache is released, so the desktop reappears as it was left.
    /// </summary>
    Pause = 1,

    /// <summary>
    /// 无处理: do nothing — the widgets keep running exactly as they would on a visible desktop.
    /// </summary>
    None = 2
}
