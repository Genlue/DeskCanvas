using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Shortcuts.Services;

/// <summary>
/// Runtime bridge: the widget view and the settings page receive host services through DI;
/// the panel and editor windows read them from here. All instances share one host process,
/// so a single last-writer-wins slot is exact.
/// </summary>
public static class ShortcutsRuntime
{
    public static IAppSettingsProvider? AppSettings { get; private set; }

    public static WidgetFactory? WidgetFactory { get; private set; }

    public static void Publish(IAppSettingsProvider? appSettings, WidgetFactory? widgetFactory)
    {
        AppSettings ??= appSettings;
        WidgetFactory ??= widgetFactory;
    }

    /// <summary>Build the per-run service bundle; <paramref name="topLevel"/> feeds clipboard and dialogs.</summary>
    public static RunServices CreateRunServices(Control? topLevelOwner) => new()
    {
        AppSettings = AppSettings,
        WidgetFactory = WidgetFactory,
        TopLevelAccessor = topLevelOwner == null
            ? null
            : () => TopLevel.GetTopLevel(topLevelOwner),
    };

    public static RunServices CreateRunServices(Func<TopLevel?> accessor) => new()
    {
        AppSettings = AppSettings,
        WidgetFactory = WidgetFactory,
        TopLevelAccessor = accessor,
    };
}
