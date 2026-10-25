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
        ResolveAppSettings();
    }

    /// <summary>
    /// The settings provider every tile paints from, resolved in two tiers: the injected one,
    /// then the host's singleton service.
    /// <para>
    /// The second tier is not decoration. A view activated through the short constructor (the
    /// parameterless one, the <c>Activator.CreateInstance</c> fallback in
    /// <c>AssemblyProvider.Activate</c>, the 组件库 preview) arrives with
    /// <c>appSettings == null</c>, and every consumer then read a null theme — which the tiles
    /// treat as "colourful". That is why monochrome only ever applied for some users/sessions:
    /// the widget kept painting each shortcut in its own colour under a monochrome theme.
    /// </para>
    /// </summary>
    public static IAppSettingsProvider? ResolveAppSettings()
    {
        if (AppSettings != null) return AppSettings;

        try
        {
            AppSettings = DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
        }
        catch
        {
            // Very early start-up / headless checks: no host container yet. Stay null rather
            // than creating a private provider (a second instance would never see a save).
        }

        return AppSettings;
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
