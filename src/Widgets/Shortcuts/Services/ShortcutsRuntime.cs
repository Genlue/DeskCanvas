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
    private static WeakReference<TopLevel>? lastTopLevel;

    public static IAppSettingsProvider? AppSettings { get; private set; }

    public static WidgetFactory? WidgetFactory { get; private set; }

    public static void Publish(IAppSettingsProvider? appSettings, WidgetFactory? widgetFactory)
    {
        AppSettings ??= appSettings;
        WidgetFactory ??= widgetFactory;
    }

    /// <summary>
    /// Remember the last visible host surface so a global hotkey can own a confirmation
    /// dialog even though it fires outside any widget's input event. Weak: a closed widget or
    /// editor must not be kept alive by this slot.
    /// </summary>
    public static void PublishTopLevel(TopLevel? topLevel)
    {
        lastTopLevel = topLevel == null ? null : new WeakReference<TopLevel>(topLevel);
    }

    /// <summary>Clear the slot only when it still points at the surface being unloaded.</summary>
    public static void ReleaseTopLevel(TopLevel? topLevel)
    {
        if (topLevel == null) return;
        if (lastTopLevel != null && lastTopLevel.TryGetTarget(out var current) &&
            ReferenceEquals(current, topLevel))
        {
            lastTopLevel = null;
        }
    }

    public static TopLevel? LastTopLevel
    {
        get
        {
            if (lastTopLevel == null) return null;
            return lastTopLevel.TryGetTarget(out var target) ? target : null;
        }
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
