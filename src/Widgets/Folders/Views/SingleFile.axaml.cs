using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using DeskCanvas.UI.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

#pragma warning disable CA1416

namespace Folders.Views;

public partial class SingleFile : UserControl, IWidgetSelfRefreshing
{
    private SingleFileModel model;
    private readonly IWidgetLayoutProvider widgetLayoutProvider;

    public SingleFile() : this(new SingleFileModel(), null!) { }

    public SingleFile(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new SingleFileModel(), widgetLayoutProvider) { }

    public SingleFile(SingleFileModel model, IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ContainerPanel.SizeChanged += (s, e) => UpdateIconSize();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        RootBorder.PointerPressed += OnPointerPressed;
        RootBorder.PointerEntered += OnPointerEntered;
        RootBorder.PointerExited += OnPointerExited;
        RootBorder.PointerCaptureLost += OnPointerCaptureLost;

        ApplyModel();
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings.HasValue)
        {
            var updated = JsonSerializer.Deserialize<SingleFileModel>(layout.Settings.Value.GetRawText());
            if (updated != null)
            {
                model = updated;
                ApplyModel();
            }
        }
    }

    /// <summary>
    /// The platform handle the OLE drop target / WM_DROPFILES subclass were registered on,
    /// captured while the window exists (<c>Loaded</c>); <c>Unloaded</c> can no longer read it
    /// (see <see cref="WidgetOleDropTarget.PruneDeadRegistrations"/> for the full story of what
    /// a skipped revoke pins).
    /// </summary>
    private IntPtr dropHandle = IntPtr.Zero;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyModel();
        if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
        {
            DragDrop.SetAllowDrop(topLevel, true);
            if (topLevel.TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
            {
                dropHandle = handle;
                WidgetOleDropTarget.Register(handle, OnOleDrop, OnOleDragActive);
                WidgetOleDropTarget.RegisterWmDropFiles(handle, OnOleDrop, OnOleDragActive);
            }
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (dropHandle != IntPtr.Zero)
        {
            WidgetOleDropTarget.Unregister(dropHandle);
            WidgetOleDropTarget.UnregisterWmDropFiles(dropHandle);
            dropHandle = IntPtr.Zero;
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateIconSize();
    }

    private void ApplyModel()
    {
        var hasValidPath = !string.IsNullOrWhiteSpace(model.Path)
                           && (File.Exists(model.Path) || Directory.Exists(model.Path));

        if (hasValidPath)
        {
            var rawIcon = FolderIconService.GetIcon(model.Path!);
            IconImage.Source = rawIcon != null ? FolderIconService.TrimTransparent(rawIcon) : null;
            IconImage.IsVisible = true;
            EmptyPlaceholder.IsVisible = false;
            ToolTip.SetTip(this, model.Path);
        }
        else
        {
            IconImage.Source = null;
            IconImage.IsVisible = false;
            EmptyPlaceholder.IsVisible = true;
            ToolTip.SetTip(this, Locale.Folders_SingleFile_EmptyHint);
        }

        UpdateIconSize();
    }

    private void UpdateIconSize()
    {
        var w = ContainerPanel.Bounds.Width > 0 ? ContainerPanel.Bounds.Width : Bounds.Width;
        var h = ContainerPanel.Bounds.Height > 0 ? ContainerPanel.Bounds.Height : Bounds.Height;
        var minSide = Math.Min(w, h);
        if (minSide <= 0) return;

        var pct = Math.Clamp(model.IconPercent, 10.0, 100.0) / 100.0;
        // Cap the base size so the hover zoom still fits the card: the transform grows the icon
        // about its centre and the card edge clips, which is why a large 图标大小 used to lose
        // its corners on hover.
        var hoverScale = model.EffectiveHoverScale;
        var iconSide = Math.Max(16.0, Math.Round(HoverZoomMath.Fit(minSide * pct, hoverScale)));

        IconImage.Width = iconSide;
        IconImage.Height = iconSide;
        EmptyPlaceholder.Width = iconSide;
        EmptyPlaceholder.Height = iconSide;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        AnimateScale(IconImage, model.EffectiveHoverScale);
        AnimateScale(EmptyPlaceholder, model.EffectiveHoverScale);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        AnimateScale(IconImage, 1.0);
        AnimateScale(EmptyPlaceholder, 1.0);
    }

    private static void AnimateScale(Control control, double scale)
    {
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        if (control.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform(1.0, 1.0);
            control.RenderTransform = transform;
        }

        transform.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = SpringMotion.CreateEasing(SpringMotion.Bounce)
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = SpringMotion.CreateEasing(SpringMotion.Bounce)
            }
        };

        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        AnimateScale(IconImage, 1.0);
        AnimateScale(EmptyPlaceholder, 1.0);
    }

    private static bool IsControlPressed(PointerPressedEventArgs e)
        => WidgetOleDropTarget.IsControlHeld(e.KeyModifiers);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;

        // Ctrl + left drag moves the widget window (same as Folder widget); never trigger click.
        if (point.Properties.IsLeftButtonPressed && IsControlPressed(e))
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.BeginMoveDrag(e);
                e.Handled = true;
            }
            return;
        }

        e.Handled = true;

        var hasValidPath = !string.IsNullOrWhiteSpace(model.Path)
                           && (File.Exists(model.Path) || Directory.Exists(model.Path));

        if (hasValidPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = model.Path!, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleFile] Failed to open {model.Path}: {ex.Message}");
            }
        }
        else
        {
            // Empty state: click prompts user to pick a file
            PickFile();
        }
    }

    private async void PickFile()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var hwnd = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var picked = ShellFilePicker.PickSingleFileNoDereference(hwnd, Locale.Folders_SingleFile_Pick);

        if (string.IsNullOrEmpty(picked) && topLevel?.StorageProvider != null)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Locale.Folders_SingleFile_Pick,
                AllowMultiple = false
            });
            if (files.Count > 0 && files[0].Path.LocalPath is { } fallbackPath)
                picked = fallbackPath;
        }

        if (!string.IsNullOrEmpty(picked))
        {
            UpdateModel(model with { Path = picked });
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            DropOverlay.IsVisible = true;
            e.Handled = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (!string.IsNullOrEmpty(path))
        {
            UpdateModel(model with { Path = path });
        }
    }

    private void OnOleDrop(List<string> paths)
    {
        DropOverlay.IsVisible = false;
        var valid = paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (!string.IsNullOrEmpty(valid))
        {
            UpdateModel(model with { Path = valid });
        }
    }

    private void OnOleDragActive(bool active)
    {
        DropOverlay.IsVisible = active;
    }

    private void UpdateModel(SingleFileModel newModel)
    {
        model = newModel;
        ApplyModel();
        if (widgetLayoutProvider == null) return;
        var newSettings = JsonSerializer.SerializeToElement(newModel);
        var newLayout = widgetLayoutProvider.Get() with { Settings = newSettings };
        widgetLayoutProvider.Save(newLayout);
    }
}
