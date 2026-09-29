using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Picture.Models;
using Picture.ViewModels;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;

namespace Picture.Views;

public partial class PictureView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable
{
    private readonly PictureViewModel viewModel;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private DateTime lastClickTime = DateTime.MinValue;

    public PictureView() : this(new PictureModel()) { }

    public PictureView(PictureModel model) : this(model, null) { }

    public PictureView(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new PictureModel(), widgetLayoutProvider) { }

    public PictureView(PictureModel model, IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        viewModel = new PictureViewModel(model);
        DataContext = viewModel;

        Classes.Add("Frameless");
        Margin = new Thickness(0);
        Padding = new Thickness(0);

        InitializeComponent();

        PointerPressed += OnPointerPressed;
        Unloaded += (_, _) => viewModel.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsControlPressed(PointerPressedEventArgs e)
    {
        return e.KeyModifiers.HasFlag(KeyModifiers.Control)
               || (GetKeyState(0x11) & 0x8000) != 0;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;

        // Ctrl + Left drag moves the widget window
        if (point.Properties.IsLeftButtonPressed && IsControlPressed(e))
        {
            if (VisualRoot is Window window)
            {
                window.BeginMoveDrag(e);
                e.Handled = true;
            }
            return;
        }

        // Double click detection
        var now = DateTime.Now;
        if ((now - lastClickTime).TotalMilliseconds < 350)
        {
            lastClickTime = DateTime.MinValue;
            if (viewModel.DoubleClickToOpen)
            {
                viewModel.OpenCurrentInShell();
                e.Handled = true;
                return;
            }
        }
        else
        {
            lastClickTime = now;
        }

        // Single click: switch to next picture
        if (viewModel.ClickToNext && viewModel.HasPictures)
        {
            viewModel.NextPicture();
            e.Handled = true;
        }
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout?.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var newModel = settings.Deserialize<PictureModel>();
                if (newModel != null)
                {
                    viewModel.ApplyModel(newModel);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    /// <summary>
    /// Stops playback and releases every decoded picture while the desktop is covered by a
    /// fullscreen or maximized window (see <c>IWidgetSuspendable</c>). Without this the GIF
    /// timer keeps driving a render pass per frame and the slideshow keeps decoding pictures
    /// that nobody can see.
    /// </summary>
    public void Suspend() => viewModel.Suspend();

    /// <summary>Re-decodes the current picture and restarts playback after <see cref="Suspend"/>.</summary>
    public void Resume() => viewModel.Resume();
}
