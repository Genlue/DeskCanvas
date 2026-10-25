using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using DeskCanvas.UI.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Views;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using Folders.Views.Settings;

#pragma warning disable CA1416

namespace Folders.Views;

public partial class SingleLink : UserControl, IWidgetSelfRefreshing
{
    private SingleLinkModel model;
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private string? lastAutoFetchedUrl;

    public SingleLink() : this(new SingleLinkModel(), null!) { }

    public SingleLink(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new SingleLinkModel(), widgetLayoutProvider) { }

    public SingleLink(SingleLinkModel model, IWidgetLayoutProvider widgetLayoutProvider)
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
            var updated = JsonSerializer.Deserialize<SingleLinkModel>(layout.Settings.Value.GetRawText());
            if (updated != null)
            {
                model = updated;
                ApplyModel();
            }
        }
    }

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
        var hasUrl = !string.IsNullOrWhiteSpace(model.Url);

        if (ContextOpenItem != null) ContextOpenItem.IsEnabled = hasUrl;
        if (ContextCopyItem != null) ContextCopyItem.IsEnabled = hasUrl;

        if (hasUrl)
        {
            var tip = !string.IsNullOrWhiteSpace(model.Title)
                ? $"{model.Title}\n{model.Url}"
                : model.Url;
            ToolTip.SetTip(this, tip);

            // 1. Try loading cached icon if CustomIconPath is specified and exists
            var icon = !string.IsNullOrWhiteSpace(model.CustomIconPath) && File.Exists(model.CustomIconPath)
                ? LinkIconService.LoadIcon(model.CustomIconPath)
                : null;

            if (icon != null)
            {
                IconImage.Source = icon;
                IconImage.IsVisible = true;
                FallbackIconContainer.IsVisible = false;
                EmptyPlaceholder.IsVisible = false;
            }
            else
            {
                IconImage.Source = null;
                IconImage.IsVisible = false;
                FallbackIconContainer.IsVisible = true;
                EmptyPlaceholder.IsVisible = false;

                // 2. If no custom icon set yet, try auto-fetching in the background
                if (string.IsNullOrWhiteSpace(model.CustomIconPath))
                {
                    TryAutoFetchFavicon();
                }
            }
        }
        else
        {
            IconImage.Source = null;
            IconImage.IsVisible = false;
            FallbackIconContainer.IsVisible = false;
            EmptyPlaceholder.IsVisible = true;
            ToolTip.SetTip(this, Locale.Folders_SingleLink_EmptyHint);
        }

        UpdateIconSize();
    }

    private async void TryAutoFetchFavicon()
    {
        if (string.IsNullOrWhiteSpace(model.Url)) return;
        if (lastAutoFetchedUrl == model.Url) return;
        lastAutoFetchedUrl = model.Url;

        var cached = await LinkIconService.FetchFaviconAsync(model.Url);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.IsNullOrWhiteSpace(model.CustomIconPath))
                {
                    UpdateModel(model with { CustomIconPath = cached });
                }
            });
        }
    }

    private void UpdateIconSize()
    {
        var w = ContainerPanel.Bounds.Width > 0 ? ContainerPanel.Bounds.Width : Bounds.Width;
        var h = ContainerPanel.Bounds.Height > 0 ? ContainerPanel.Bounds.Height : Bounds.Height;
        var minSide = Math.Min(w, h);
        if (minSide <= 0) return;

        var pct = Math.Clamp(model.IconPercent, 10.0, 100.0) / 100.0;
        // Cap the base size so the hover zoom still fits the card (the transform grows the icon
        // about its centre, and the card edge clips).
        var iconSide = Math.Max(16.0, Math.Round(HoverZoomMath.Fit(minSide * pct, model.EffectiveHoverScale)));

        IconImage.Width = iconSide;
        IconImage.Height = iconSide;
        EmptyPlaceholder.Width = iconSide;
        EmptyPlaceholder.Height = iconSide;
        FallbackIconContainer.Width = iconSide;
        FallbackIconContainer.Height = iconSide;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        AnimateScale(IconImage, model.EffectiveHoverScale);
        AnimateScale(EmptyPlaceholder, model.EffectiveHoverScale);
        AnimateScale(FallbackIconContainer, model.EffectiveHoverScale);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        AnimateScale(IconImage, 1.0);
        AnimateScale(EmptyPlaceholder, 1.0);
        AnimateScale(FallbackIconContainer, 1.0);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        AnimateScale(IconImage, 1.0);
        AnimateScale(EmptyPlaceholder, 1.0);
        AnimateScale(FallbackIconContainer, 1.0);
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

    private static bool IsControlPressed(PointerPressedEventArgs e)
        => WidgetOleDropTarget.IsControlHeld(e.KeyModifiers);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;

        // Ctrl + left drag moves the widget window; never trigger click.
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

        if (!string.IsNullOrWhiteSpace(model.Url))
        {
            OpenLink();
        }
        else
        {
            OpenSettings();
        }
    }

    public void OpenLink()
    {
        if (string.IsNullOrWhiteSpace(model.Url))
        {
            OpenSettings();
            return;
        }

        var targetUrl = EnsureProtocol(model.Url);

        try
        {
            if (!string.IsNullOrWhiteSpace(model.BrowserPath) && File.Exists(model.BrowserPath))
            {
                Process.Start(new ProcessStartInfo(model.BrowserPath, targetUrl) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SingleLink] Failed to open {targetUrl}: {ex.Message}");
        }
    }

    public void OpenSettings()
    {
        if (TopLevel.GetTopLevel(this) is DeskCanvas.Views.Widget widget)
        {
            widget.EditWidget();
        }
        else if (widgetLayoutProvider != null)
        {
            var editWindow = new EditWidget(widgetLayoutProvider, new SingleLinkSettings(widgetLayoutProvider));
            editWindow.Show();
        }
    }

    private static string EnsureProtocol(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        return $"https://{trimmed}";
    }

    private void OnOpenLinkContextClick(object? sender, RoutedEventArgs e)
    {
        OpenLink();
    }

    private async void OnCopyLinkContextClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(model.Url)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(model.Url);
        }
    }

    private void OnEditContextClick(object? sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text) || e.DataTransfer.Contains(DataFormat.File))
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

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;

        // Try text URL drop
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            var text = e.DataTransfer.TryGetText()?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                UpdateModel(model with { Url = text });
                return;
            }
        }

        // Try file drop (e.g. .url internet shortcut, image file)
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).ToList();
            if (paths != null && paths.Count > 0)
            {
                await HandleFileDropAsync(paths[0]);
            }
        }
    }

    private async void OnOleDrop(List<string> paths)
    {
        DropOverlay.IsVisible = false;
        if (paths.Count > 0)
        {
            await HandleFileDropAsync(paths[0]);
        }
    }

    private async Task HandleFileDropAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // 1. Internet shortcut (.url)
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var lines = await File.ReadAllLinesAsync(path);
            var urlLine = lines.FirstOrDefault(l => l.Trim().StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(urlLine))
            {
                var url = urlLine.Trim()["URL=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    UpdateModel(model with { Url = url });
                    return;
                }
            }
        }

        // 2. Image dropped as custom icon
        var ext = Path.GetExtension(path);
        if (ext is ".ico" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".svg")
        {
            var saved = await LinkIconService.SaveLocalFileAsync(path);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                UpdateModel(model with { CustomIconPath = saved });
            }
        }
    }

    private void OnOleDragActive(bool active)
    {
        DropOverlay.IsVisible = active;
    }

    private void UpdateModel(SingleLinkModel newModel)
    {
        model = newModel;
        ApplyModel();
        if (widgetLayoutProvider == null) return;
        var newSettings = JsonSerializer.SerializeToElement(newModel);
        var newLayout = widgetLayoutProvider.Get() with { Settings = newSettings };
        widgetLayoutProvider.Save(newLayout);
    }
}
