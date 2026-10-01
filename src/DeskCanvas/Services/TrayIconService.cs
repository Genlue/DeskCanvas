using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
// Alias: "Settings" alone is ambiguous with the DeskCanvas.Core.Models.Settings namespace.
using SettingsWindow = DeskCanvas.Views.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Notification-area (tray) icon and its right-click menu.
/// <para>
/// The app lives on the desktop with no main window, so the tray is the only always-reachable way
/// back to it (and the only way to quit with widgets on screen). The icon can be hidden from its
/// own menu; the widget context menu switches it back on, so hiding it is never a dead end.
/// </para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string IconUri = "avares://DeskCanvas/Assets/icon.ico";

    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly Func<SettingsWindow> settingsWindow;
    private readonly WidgetFactory widgetFactory;

    private TrayIcon? trayIcon;
    private NativeMenuItem? widgetsItem;

    public TrayIconService(IAppSettingsProvider appSettingsProvider, Func<SettingsWindow> settingsWindow,
        WidgetFactory widgetFactory)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.settingsWindow = settingsWindow;
        this.widgetFactory = widgetFactory;
    }

    /// <summary>The icon is on screen (the setting says so and it could actually be created).</summary>
    public bool IsIconVisible => trayIcon?.IsVisible == true;

    public void Start()
    {
        if (trayIcon != null) return;

        var application = Application.Current;
        if (application == null) return;

        var menu = BuildMenu();

        trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "DeskCanvas+",
            Menu = menu,
            IsVisible = appSettingsProvider.Get().ShowTrayIcon
        };

        // A left click is the shortest way back into the app.
        trayIcon.Clicked += (_, _) => OpenSettings();

        TrayIcon.SetIcons(application, new TrayIcons { trayIcon });

        // The widget context menu can switch the icon back on; follow the setting.
        appSettingsProvider.DataChanged += OnSettingsChanged;

        // Ensure tray popup menu renders as a solid opaque card:
        // - Window transparency set to Transparent (not AcrylicBlur from Styles/Transparent.axaml)
        // - Presenter configured with 100% opaque solid colors (White in light, #1C1C1E in dark)
        // - Disable Windows 11 DWM outer small-radius border rectangle
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            try
            {
                if (window.GetType().FullName == "Avalonia.Win32.TrayIconImpl+TrayPopupRoot")
                {
                    window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                    window.Background = Brushes.Transparent;

                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                            if (handle != IntPtr.Zero)
                            {
                                InteropService.DisableWindowBorder(handle);
                            }
                        }
                        catch { }
                    }, DispatcherPriority.Render);

                    if (window.Content is MenuFlyoutPresenter presenter)
                    {
                        ConfigureTrayPresenter(presenter);
                    }
                }
            }
            catch { }
        });
    }

    private static void ConfigureTrayPresenter(MenuFlyoutPresenter presenter)
    {
        try
        {
            var isDark = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            presenter.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            presenter.BorderBrush = new SolidColorBrush(isDark ? Color.Parse("#38383A") : Color.Parse("#E5E5EA"));
            presenter.BorderThickness = new Thickness(1);
            presenter.CornerRadius = new CornerRadius(8);
            presenter.Padding = new Thickness(4);
            presenter.ClipToBounds = true;
        }
        catch { }
    }

    private NativeMenu BuildMenu()
    {
        widgetsItem = new NativeMenuItem(WidgetsMenuTitle());
        widgetsItem.Click += (_, _) => ToggleWidgets();

        var openItem = new NativeMenuItem(Locale.Tray_OpenSettings);
        openItem.Click += (_, _) => OpenSettings();

        var hideIconItem = new NativeMenuItem(Locale.Tray_HideIcon);
        hideIconItem.Click += (_, _) => SetIconVisible(false);

        var exitItem = new NativeMenuItem(Locale.Tray_Exit);
        exitItem.Click += (_, _) => AppShutdown.Request();

        var menu = new NativeMenu();
        menu.Add(openItem);
        menu.Add(widgetsItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(hideIconItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);
        return menu;
    }

    private void OpenSettings()
    {
        var window = settingsWindow();
        window.ShowAndActivate();
    }

    private void ToggleWidgets()
    {
        widgetFactory.SetWidgetsHidden(!widgetFactory.WidgetsHidden);
        UpdateWidgetsItem();
    }

    /// <summary>Label of the widget toggle: it always describes what the next click does.</summary>
    private string WidgetsMenuTitle() =>
        widgetFactory.WidgetsHidden ? Locale.Tray_ShowWidgets : Locale.Tray_HideWidgets;

    private void UpdateWidgetsItem()
    {
        if (widgetsItem != null) widgetsItem.Header = WidgetsMenuTitle();
    }

    /// <summary>Show or hide the tray icon and remember the choice.</summary>
    public void SetIconVisible(bool visible)
    {
        var settings = appSettingsProvider.Get();
        if (settings.ShowTrayIcon == visible) return;

        appSettingsProvider.Save(settings with { ShowTrayIcon = visible });
    }

    private void OnSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        if (trayIcon == null) return;
        if (oldData?.ShowTrayIcon == newData.ShowTrayIcon) return;

        trayIcon.IsVisible = newData.ShowTrayIcon;
    }

    /// <summary>
    /// The tray icon of the shipped exe. A failure here must not take the app down — the icon
    /// then simply stays blank instead of the process failing to start.
    /// </summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        appSettingsProvider.DataChanged -= OnSettingsChanged;

        if (trayIcon != null)
        {
            if (Application.Current != null)
                TrayIcon.SetIcons(Application.Current, null);

            trayIcon.Dispose();
            trayIcon = null;
        }
    }
}
