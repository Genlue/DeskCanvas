using System;
using System.Diagnostics;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.WinUI.WidgetEditors.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// 组件编辑窗口（报告 §3 第三步 S3.3）：普通应用窗口、可调整大小、初始客户区 560×640。
/// 正文由<b>一</b>个滚动容器承载（页面自己不再套 ScrollViewer）。
/// <para>
/// <b>几何记忆</b>：复用 <see cref="WindowGeometryStore"/>，键仍是旧的
/// <c>"assembly:settingsViewFullName"</c>（由创建请求的一方按 <c>EditModelViewType</c> 原类型算好，
/// 即 <see cref="WidgetEditorRequest.SettingsViewKey"/>）。旧入口写的 ui-state.json 条目继续命中。
/// </para>
/// <para>
/// <b>上下文</b>：<c>IWidgetLayoutProvider</c> 是桌面组件自己的实例（由 <see cref="WidgetEditorHost"/>
/// 按 InstanceId 解析并复用 generation），因此页面保存后组件立即刷新，存档仍是原 layout.json/JSON。
/// </para>
/// <para>
/// <b>生命周期</b>：关闭 = 真正关闭（与设置窗口的“隐藏”不同）；关闭前 Detach 页面并保存几何。
/// </para>
/// </summary>
public sealed partial class EditWidgetWindow : Window
{
    /// <summary>旧 EditWidget 的初始尺寸（DIP，客户区）。</summary>
    private const double InitialWidthDip = 560;
    private const double InitialHeightDip = 640;

    private readonly WidgetEditorRequest request;
    private readonly WidgetEditorRegistration registration;
    private readonly string geometryKey;

    private IWidgetEditorPage? page;
    private bool geometrySettled;
    private bool closeHandled;
    private Microsoft.UI.Xaml.XamlRoot? watchedRoot;
    private double pendingWidthDip = InitialWidthDip;
    private double pendingHeightDip = InitialHeightDip;
    private double appliedScale = 1.0;

    /// <summary>
    /// 目标外框中心（物理像素）。恢复位置要在窗口真正显示之后再应用一次：
    /// WinUI 首次显示会按自己的初值把窗口摆到屏幕中央，覆盖显示前的 SetWindowPos
    /// （实测保存的 (300,200) 被改回居中），所以这里记住目标中心，Loaded 时再钉一次。
    /// </summary>
    private int pendingCenterX;
    private int pendingCenterY;

    internal EditWidgetWindow(
        IServiceProvider services,
        IWidgetLayoutProvider layoutProvider,
        WidgetEditorRequest request,
        WidgetEditorRegistration registration)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.registration = registration ?? throw new ArgumentNullException(nameof(registration));
        geometryKey = request.SettingsViewKey ?? string.Empty;

        InitializeComponent();

        Title = $"{request.AssemblyName} {request.SubType}";
        TitleText.Text = request.AssemblyName;
        SubtitleText.Text = request.SubType;
        SaveButton.Content = Locale.Widget_Save;

        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnWindowActivated;
        if (Content is FrameworkElement root) root.Loaded += OnRootLoaded;

        var context = new WidgetEditorContext(services, layoutProvider, request, this);
        page = CreatePage(context);

        if (page is not null)
        {
            PageHost.Content = page.View;
            try
            {
                page.Attach();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Editor] page attach failed: {ex}");
                AppStartup.LogDiagnostic(
                    $"widget editor page attach failed for {request.AssemblyName}/{request.SubType}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        ApplyInitialGeometry();
    }

    /// <summary>窗口被真正关闭（不是隐藏）时触发，宿主据此丢掉失效引用。</summary>
    internal event EventHandler? ClosedForGood;

    /// <summary>本次编辑使用的几何槽位键（诊断日志用）。</summary>
    internal string GeometryKey => geometryKey;

    /// <summary>窗口已经存在时再次请求编辑：还原/激活同一个窗口，不叠第二个。</summary>
    internal void BringToFront()
    {
        var hwnd = EditorWindowNative.GetHandle(this);
        EditorWindowNative.BringToFront(hwnd);
        Activate();
    }

    /// <summary>页面保存失败时显示在窗口顶部（不是静默失败，也不关窗伪装成功）。</summary>
    internal void ShowSaveError(string message)
    {
        AppStartup.LogDiagnostic(
            $"widget editor save failed for {request.AssemblyName}/{request.SubType}: {message}");

        StatusBar.Title = WidgetEditorStrings.SaveFailedTitle;
        StatusBar.Message = message ?? string.Empty;
        StatusBar.IsOpen = true;
    }

    private IWidgetEditorPage CreatePage(WidgetEditorContext context)
    {
        if (registration.Factory is { } factory)
        {
            try
            {
                return factory(context);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Editor] page factory failed: {ex}");
                AppStartup.LogDiagnostic(
                    $"widget editor page factory failed for {registration.Key}: {ex.GetType().Name}: {ex.Message}");
                return new NotMigratedEditorPage(context, registration.State, ex.Message, unavailable: true);
            }
        }

        // 未迁移 / 插件需适配：显示明确的说明页，绝不回退到 Avalonia 设置页。
        return new NotMigratedEditorPage(context, registration.State);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Close();

    // ---- 几何 -------------------------------------------------------------------------------

    /// <summary>
    /// 首次显示前的几何：有存档就按旧槽位恢复（DIP 尺寸 + 工作区相对中心，夹在该屏工作区内、
    /// 负坐标保持），没有就按 560×640 居中到主屏工作区。DPI 用目标显示器的有效 DPI 估算。
    /// </summary>
    private void ApplyInitialGeometry()
    {
        var hwnd = EditorWindowNative.GetHandle(this);
        if (hwnd == IntPtr.Zero) return;

        var entry = string.IsNullOrEmpty(geometryKey) ? null : WindowGeometryStore.TryGet(geometryKey);
        if (entry is not null)
        {
            pendingWidthDip = entry.WidthDip;
            pendingHeightDip = entry.HeightDip;

            var monitor = EditorWindowNative.FindMonitor(entry.MonitorId) ?? EditorWindowNative.PrimaryMonitor();
            appliedScale = EstimateScale(hwnd, monitor);
            pendingCenterX = (int)Math.Round(monitor.WorkX + entry.CenterXRatio * monitor.WorkWidth);
            pendingCenterY = (int)Math.Round(monitor.WorkY + entry.CenterYRatio * monitor.WorkHeight);

            EditorWindowNative.ApplyClientGeometry(hwnd, pendingWidthDip, pendingHeightDip,
                pendingCenterX, pendingCenterY, appliedScale, monitor);
            return;
        }

        var primary = EditorWindowNative.PrimaryMonitor();
        appliedScale = EstimateScale(hwnd, primary);
        pendingCenterX = primary.WorkX + primary.WorkWidth / 2;
        pendingCenterY = primary.WorkY + primary.WorkHeight / 2;
        EditorWindowNative.ApplyClientGeometry(hwnd, pendingWidthDip, pendingHeightDip,
            pendingCenterX, pendingCenterY, appliedScale, primary);
    }

    /// <summary>
    /// 窗口创建时的缩放估算：显示器有效 DPI 优先；若 <c>GetDpiForMonitor</c> 。
    /// PerMonitorV2 下只给出 96，则退回窗口自己的 <c>GetDpiForWindow</c>。
    /// 真正的修正发生在 <see cref="TryCorrectScale"/>（以 <c>XamlRoot.RasterizationScale</c> 为准）。
    /// </summary>
    private static double EstimateScale(IntPtr hwnd, EditorMonitor monitor)
    {
        var dpi = monitor.Dpi;
        if (dpi <= 96)
        {
            var windowDpi = EditorWindowNative.GetWindowDpi(hwnd);
            if (windowDpi > 96) dpi = windowDpi;
        }

        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>窗口激活时顺手试一次缩放校正（通常此时 XamlRoot 还没就绪，真正钉几何在 Loaded）。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        TryCorrectScale();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (geometrySettled) return;

        if (Content?.XamlRoot is { } root)
        {
            watchedRoot = root;
            root.Changed += OnXamlRootChanged;
        }

        TryCorrectScale();

        if (geometrySettled) return;

        // 兜底：XamlRoot 在本轮之后才挂上来时也要校正一次（与 SettingsWindow 的做法一致）。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TryCorrectScale();
        };
        timer.Start();
    }

    private void OnXamlRootChanged(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        TryCorrectScale();
        if (geometrySettled) sender.Changed -= OnXamlRootChanged;
    }

    /// <summary>
    /// 首次布局后按 WinUI 真正使用的缩放（<c>XamlRoot.RasterizationScale</c>）把几何钉一次：
    /// 窗口创建时还没落到显示器上，<c>GetDpiForWindow/GetDpiForMonitor</c> 可能只给出 96。
    /// WinUI 首次显示还会用自己的初值覆盖显示前摆好的尺寸/位置。XamlRoot 没就绪时靠 Changed/1 秒兜底。
    /// </summary>
    private void TryCorrectScale()
    {
        if (geometrySettled) return;

        var scale = Content?.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0) return; // XamlRoot 还没就绪，等 Changed/兜底重试

        var hwnd = EditorWindowNative.GetHandle(this);
        if (hwnd == IntPtr.Zero) return;

        var before = EditorWindowNative.GetClientSize(hwnd);
        var outer = EditorWindowNative.GetOuterRect(hwnd);
        if (outer.Width <= 0 || outer.Height <= 0) return;

        geometrySettled = true;
        if (watchedRoot is { } root)
        {
            root.Changed -= OnXamlRootChanged;
            watchedRoot = null;
        }

        // 尺寸与位置都按打开时的目标重钉一次：WinUI 首次显示会用自己的初值覆盖显示前的摆放。
        var monitor = EditorWindowNative.MonitorOf(hwnd);
        appliedScale = scale;
        EditorWindowNative.ApplyClientGeometry(hwnd, pendingWidthDip, pendingHeightDip,
            pendingCenterX, pendingCenterY, scale, monitor);

        var after = EditorWindowNative.GetClientSize(hwnd);
        if (before.Width != after.Width || before.Height != after.Height)
        {
            AppStartup.LogDiagnostic(
                $"editor window: corrected client {before.Width}x{before.Height} -> {after.Width}x{after.Height} " +
                $"(target {pendingWidthDip:0.#}x{pendingHeightDip:0.#} DIP, scale {scale:0.##}).");
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            SaveGeometry();
        }
        catch (Exception ex)
        {
            // UI 状态写入失败绝不阻止关窗（与旧 EditWidget.OnClosing 同一语义）。
            Debug.WriteLine($"[Editor] saving geometry failed: {ex.Message}");
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (closeHandled) return;
        closeHandled = true;

        try
        {
            AppWindow.Closing -= OnClosing;
        }
        catch (Exception)
        {
            // 窗口销毁后 AppWindow 可能已不可用；失败无关紧要。
        }

        DetachPage();
        ClosedForGood?.Invoke(this, EventArgs.Empty);
    }

    private void DetachPage()
    {
        var current = page;
        page = null;
        if (current is null) return;

        try
        {
            current.Detach();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Editor] page detach failed: {ex.Message}");
        }

        if (current is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Editor] page dispose failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 保存普通几何：客户区 DIP 尺寸 + 所在显示器签名 + 工作区相对中心（与旧 <c>EditWidget.SaveGeometry</c>
    /// 同一数据格式，因此旧条目可读、新条目旧入口也可读）。
    /// </summary>
    private void SaveGeometry()
    {
        if (string.IsNullOrEmpty(geometryKey)) return;

        var hwnd = EditorWindowNative.GetHandle(this);
        if (hwnd == IntPtr.Zero) return;
        if (EditorWindowNative.IsMinimized(hwnd)) return;

        var client = EditorWindowNative.GetClientSize(hwnd);
        if (client.Width <= 0 || client.Height <= 0) return;

        var monitor = EditorWindowNative.MonitorOf(hwnd);
        if (monitor.WorkWidth <= 0 || monitor.WorkHeight <= 0) return;

        var dpi = EditorWindowNative.GetWindowDpi(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : appliedScale;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;

        var outer = EditorWindowNative.GetOuterRect(hwnd);
        var centerX = outer.X + outer.Width / 2.0;
        var centerY = outer.Y + outer.Height / 2.0;

        WindowGeometryStore.Save(geometryKey, new WindowGeometryEntry
        {
            WidthDip = Math.Round(client.Width / scale, 1),
            HeightDip = Math.Round(client.Height / scale, 1),
            MonitorId = monitor.Id,
            CenterXRatio = Math.Clamp((centerX - monitor.WorkX) / monitor.WorkWidth, 0, 1),
            CenterYRatio = Math.Clamp((centerY - monitor.WorkY) / monitor.WorkHeight, 0, 1),
        });
    }
}
