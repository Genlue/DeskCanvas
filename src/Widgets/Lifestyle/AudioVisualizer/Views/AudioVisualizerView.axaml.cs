using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Views;
using AudioVisualizer.Models;
using AudioVisualizer.Services;

namespace AudioVisualizer.Views;

/// <summary>
/// 音频律动桌面小组件：全尺寸自适应渲染，支持隐藏背景悬浮与各种自定义律动路径
/// </summary>
public partial class AudioVisualizerView : UserControl, IFramelessWidget, IWidgetSuspendable, IDisposable
{
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private AudioVisualizerModel model;
    private DispatcherTimer? renderTimer;
    private readonly float[] spectrumBuffer = new float[64];
    private bool isRetainedInService;
    private bool isSuspended;

    /// <inheritdoc />
    public bool IsFrameless => model.IsFrameless;

    public AudioVisualizerView()
        : this(null, new AudioVisualizerModel())
    {
    }

    public AudioVisualizerView(IWidgetLayoutProvider? widgetLayoutProvider, AudioVisualizerModel? initialModel)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.model = (initialModel ?? widgetLayoutProvider?.Get()?.GetModel<AudioVisualizerModel>() ?? new AudioVisualizerModel()).Normalize();

        InitializeComponent();

        if (model.IsFrameless)
        {
            Classes.Add("Frameless");
        }

        Canvas.Model = model;

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SizeChanged += OnSizeChanged;

        if (widgetLayoutProvider != null)
        {
            widgetLayoutProvider.DataChanged += OnLayoutUpdated;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SyncFramelessWindowState();
        StartAudioCapture();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        StopAudioCapture();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Canvas.InvalidateGeometryCache();
    }

    private void OnLayoutUpdated(object sender, DeskCanvas.Core.Models.WidgetLayout? oldLayout, DeskCanvas.Core.Models.WidgetLayout layout)
    {
        var next = layout.GetModel<AudioVisualizerModel>()?.Normalize() ?? new AudioVisualizerModel().Normalize();
        bool sourceChanged = next.AudioSource != model.AudioSource;
        bool framelessChanged = next.IsFrameless != model.IsFrameless;

        model = next;
        Canvas.Model = model;

        if (framelessChanged)
        {
            SyncFramelessWindowState();
        }

        if (sourceChanged && isRetainedInService)
        {
            AudioCaptureService.Instance.SwitchSource(model.AudioSource);
        }
    }

    private void SyncFramelessWindowState()
    {
        if (model.IsFrameless)
        {
            if (!Classes.Contains("Frameless"))
                Classes.Add("Frameless");
            Padding = new Thickness(0);
        }
        else
        {
            Classes.Remove("Frameless");
            Padding = new Thickness(0);
        }

        (TopLevel.GetTopLevel(this) as Widget)?.RefreshFramelessState();
    }

    private void StartAudioCapture()
    {
        if (isSuspended) return;

        if (!isRetainedInService)
        {
            AudioCaptureService.Instance.Retain(model.AudioSource);
            isRetainedInService = true;
        }

        if (renderTimer == null)
        {
            renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnRenderTick);
            renderTimer.Start();
        }
    }

    private void StopAudioCapture()
    {
        if (renderTimer != null)
        {
            renderTimer.Stop();
            renderTimer = null;
        }

        if (isRetainedInService)
        {
            AudioCaptureService.Instance.Release();
            isRetainedInService = false;
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (isSuspended) return;

        AudioCaptureService.Instance.ReadSpectrum(spectrumBuffer, model.Sensitivity, model.Smoothing);
        Canvas.UpdateSpectrum(spectrumBuffer);
    }

    /// <inheritdoc />
    public void Suspend()
    {
        isSuspended = true;
        StopAudioCapture();
    }

    /// <inheritdoc />
    public void Resume()
    {
        isSuspended = false;
        StartAudioCapture();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopAudioCapture();
        if (widgetLayoutProvider != null)
        {
            widgetLayoutProvider.DataChanged -= OnLayoutUpdated;
        }
    }
}
