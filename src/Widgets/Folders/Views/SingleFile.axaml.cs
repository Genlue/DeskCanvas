using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
    private const double HoverScale = 1.08;

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

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyModel();
        if (VisualRoot is TopLevel topLevel)
        {
            DragDrop.SetAllowDrop(topLevel, true);
            if (topLevel.TryGetPlatformHandle()?.Handle is { } handle)
            {
                WidgetOleDropTarget.Register(handle, OnOleDrop, OnOleDragActive);
                WidgetOleDropTarget.RegisterWmDropFiles(handle, OnOleDrop, OnOleDragActive);
            }
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is TopLevel topLevel && topLevel.TryGetPlatformHandle()?.Handle is { } handle)
        {
            WidgetOleDropTarget.Unregister(handle);
            WidgetOleDropTarget.UnregisterWmDropFiles(handle);
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
            IconImage.Source = FolderIconService.GetIcon(model.Path!);
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
        var w = ContainerPanel.Bounds.Width;
        var h = ContainerPanel.Bounds.Height;
        var minSide = Math.Min(w, h);
        if (minSide <= 0) return;

        var pct = Math.Clamp(model.IconPercent, 10.0, 100.0) / 100.0;
        var iconSide = Math.Max(16.0, Math.Round(minSide * pct));

        IconImage.Width = iconSide;
        IconImage.Height = iconSide;
        EmptyPlaceholder.Width = iconSide;
        EmptyPlaceholder.Height = iconSide;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        AnimateScale(IconImage, HoverScale);
        AnimateScale(EmptyPlaceholder, HoverScale);
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
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut()
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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsControlPressed(PointerPressedEventArgs e)
    {
        return e.KeyModifiers.HasFlag(KeyModifiers.Control)
               || (GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;

        // Ctrl + left drag moves the widget window (same as Folder widget); never trigger click.
        if (point.Properties.IsLeftButtonPressed && IsControlPressed(e))
        {
            if (VisualRoot is Window window)
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
        if (e.Data.Contains(DataFormats.FileNames))
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
        if (!e.Data.Contains(DataFormats.FileNames)) return;

        var path = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
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
