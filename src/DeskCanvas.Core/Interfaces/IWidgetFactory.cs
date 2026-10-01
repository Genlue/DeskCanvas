using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Factory for creating widgets.
/// </summary>
/// <typeparam name="TWindow">Type of window. For Avalonia, use <c>Avalonia.Controls.Window</c></typeparam>
/// <typeparam name="TControl">Type of user control. For Avalonia, use <c>Avalonia.Controls.UserControl</c></typeparam>
public interface IWidgetFactory<out TWindow, out TControl>
{
    /// <summary>
    /// Creates widgets from current layout settings.
    /// </summary>
    /// <returns>Collection of windows.</returns>
    public IEnumerable<TWindow> Create();
    
    /// <summary>
    /// Creates a user control of the specified type.
    /// <para>May be used to show widget's content in another window.</para>
    /// </summary>
    /// <param name="type">Type of user control.</param>
    /// <returns>Activated user control.</returns>
    public TControl CreateControl(Type type);
    
    /// <summary>
    /// Creates a widget from a specified layout, and adds it to the collection.
    /// </summary>
    /// <param name="widgetLayout">Layout for the widget.</param>
    /// <returns>Activated window.</returns>
    public TWindow Add(WidgetLayout widgetLayout);

    /// <summary>
    /// Creates a widget on a specific screen configuration (multi-screen), and
    /// adds it to the collection. The screen entry is upserted if new.
    /// </summary>
    /// <param name="screen">The target screen configuration.</param>
    /// <param name="widgetLayout">Layout for the widget (relative to that screen's working area).</param>
    /// <returns>Activated window.</returns>
    public TWindow Add(ScreenLayout screen, WidgetLayout widgetLayout);

    /// <summary>
    /// Closes all active widget windows and recreates them from the current layout.
    /// </summary>
    public void RecreateAll();

    /// <summary>
    /// Closes all active widget windows <b>without</b> creating new ones.
    /// <para>
    /// Call this <i>before</i> replacing the stored layout (profile switch, import):
    /// the closing windows keep their layout-change subscriptions alive until they are
    /// actually gone, and a still-alive window would write its own entry back into the
    /// freshly loaded layout — resurrecting the widgets of the outgoing configuration.
    /// </para>
    /// </summary>
    public void CloseAll();

    /// <summary>
    /// Creates and shows widget windows for every screen in the current layout.
    /// The counterpart of <see cref="CloseAll"/> (and the second half of
    /// <see cref="RecreateAll"/>).
    /// </summary>
    public void CreateFromLayout();

    /// <summary>
    /// Release the caches the widget views opt to release (see <see cref="IWidgetSuspendable"/>)
    /// and pause the shared widget timers — used while a fullscreen or maximized application
    /// covers <b>every</b> attached screen. The windows stay visible in place (they are already
    /// behind whatever covers the screens), so leaving the covered state is instant.
    /// </summary>
    public void SuspendAll();

    /// <summary>Undo <see cref="SuspendAll"/>: resume the timers and rebuild the released caches.</summary>
    public void ResumeAll();
}