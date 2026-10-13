using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Views;

public partial class EditWidget : Window
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    public UserControl Control { get; }
    public string WidgetType => widgetLayoutProvider.Get().Type;
    public string WidgetSubType => widgetLayoutProvider.Get().SubType;

    /// <summary>True once the first-show visibility rescue ran; later activations must not re-place the window.</summary>
    private bool geometryChecked;

    public EditWidget(IWidgetLayoutProvider widgetLayoutProvider, UserControl control)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        DataContext = this;
        Control = control;
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Only the FIRST show may touch the position. The old code clamped X/Y to 0 on every
        // activation, which destroyed the legitimate negative coordinates of monitors placed
        // left of / above the primary — and re-placing on each activation also fought the user
        // moving the window between screens.
        if (geometryChecked) return;
        geometryChecked = true;
        RescueIfOffScreen();
    }

    /// <summary>
    /// Keeps a first-show window reachable: if its rectangle intersects no screen's working
    /// area (e.g. the screen it was placed on is gone), pull it inside the primary working
    /// area. Legal negative coordinates are preserved untouched.
    /// </summary>
    private void RescueIfOffScreen()
    {
        var screens = Screens;
        if (screens is null || screens.All.Count == 0) return;

        var areas = new PixelRect[screens.All.Count];
        for (var i = 0; i < screens.All.Count; i++) areas[i] = screens.All[i].WorkingArea;
        var primary = (screens.Primary ?? screens.All[0]).WorkingArea;

        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        Position = ResolveVisiblePosition(Position, size, areas, primary);
    }

    /// <summary>
    /// Pure visibility rule (exposed for the foundation checks): a window rectangle that
    /// intersects ANY working area keeps its position exactly — negative coordinates of
    /// monitors left of / above the primary included; otherwise it is clamped inside the
    /// primary working area so at least a meaningful part stays reachable.
    /// </summary>
    public static PixelPoint ResolveVisiblePosition(PixelPoint position, PixelSize size,
        IReadOnlyList<PixelRect> workingAreas, PixelRect primary)
    {
        var rect = new PixelRect(position, size);
        foreach (var area in workingAreas)
        {
            if (area.Intersects(rect)) return position;
        }

        var x = Math.Clamp(position.X, primary.X, Math.Max(primary.X, primary.Right - size.Width));
        var y = Math.Clamp(position.Y, primary.Y, Math.Max(primary.Y, primary.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    private void Close(object? sender, RoutedEventArgs e) => Close();
}
