using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using DeskCanvas.Services;
using AvDragDropEffects = Avalonia.Input.DragDropEffects;
using AvIDataTransfer = Avalonia.Input.IDataTransfer;
using AvIPlatformDragSource = Avalonia.Input.Platform.IPlatformDragSource;
using AvPointerPressedEventArgs = Avalonia.Input.PointerPressedEventArgs;
using AvTopLevel = Avalonia.Controls.TopLevel;
using AvVisual = Avalonia.Visual;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 拖放服务路由：保留 Win32 的 <see cref="AvIPlatformDragSource"/> 给桌面组件，
/// 把嵌入的 <c>AvaloniaSwapChainPanel</c> 交给集成包自己的 WinUI 拖放实现。
/// <para>
/// <c>AvaloniaSwapChainPanel</c> 的静态构造会把全局 <c>IPlatformDragSource</c> 换成
/// <c>WinUIDragSource</c>（只认识嵌入面板）。这里做两件事：
/// 1. 平台初始化后、任何面板创建前保存原 Win32 服务；
/// 2. 面板创建后（全局绑定被换成 WinUIDragSource 时）把它保存为“嵌入专用服务”，再把路由装回全局。
/// 这样普通桌面组件的拖放仍是原生 Win32 行为，而嵌入预览仍走集成包的 <c>StartDragAsync</c>。
/// </para>
/// <para>
/// Avalonia 12 把 <c>AvaloniaLocator</c>/<c>IPlatformDragSource</c> 标成 <c>[PrivateApi]</c>：
/// 引用程序集里不仅接口方法不可直接调用，还带了一个用户代码无法实现的哨兵成员。为了避免
/// 在整个 WinUI 工程打开 <c>AvaloniaAccessUnstablePrivateApis</c>（会改变所有执行者的编译
/// 参考程序集），本文件只在运行时通过反射访问 locator，并用 <see cref="DispatchProxy"/> 生成
/// 路由实现；不复制集成包的拖放代码。
/// </para>
/// <para>
/// TODO(第五步/组件库)：集成包没有公开“替换前的服务”，只能在面板静态构造前保存。
/// 因此每次 <c>new AvaloniaSwapChainPanel()</c> 之后必须调用一次
/// <see cref="EnsureInstalled"/>（或 <see cref="RegisterEmbeddedTopLevel"/> 时会顺带调用）；
/// 否则全局绑定会停留在 WinUIDragSource，桌面组件拖放会返回 None。
/// 本步还没有嵌入面板，路由骨架先就位。
/// </para>
/// </summary>
internal static class DragSourceRouter
{
    private const string EmbeddedRootTypeName = "Avalonia.Controls.Embedding.EmbeddableControlRoot";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<AvTopLevel, byte> EmbeddedTopLevels = new();

    private static AvIPlatformDragSource? original;
    private static AvIPlatformDragSource? embedded;
    private static AvIPlatformDragSource? router;
    private static bool reportedAccessFailure;
    private static bool reportedInstall;

    /// <summary>保存 Win32 平台的原始拖放实现；只在本进程第一次调用时生效。</summary>
    public static void CaptureOriginal()
    {
        lock (Gate)
        {
            if (original is not null) return;
            original = LocatorBridge.GetDragSource();
        }
    }

    /// <summary>
    /// 确保全局绑定是路由实现；若发现集成包刚把绑定换成 WinUIDragSource，先把它记为嵌入专用服务。

    /// 每次创建 <c>AvaloniaSwapChainPanel</c> 后调用（见类注释的 TODO）。

    /// </summary>
    public static void EnsureInstalled()
    {
        lock (Gate)
        {
            var current = LocatorBridge.GetDragSource();
            if (router is not null && ReferenceEquals(current, router)) return;

            if (current is not null && !ReferenceEquals(current, original))
                embedded = current;

            router ??= CreateRouter();
            if (router is null)
            {
                ReportAccessFailure("could not create the drag-source router proxy");
                return;
            }

            if (!LocatorBridge.BindConstant(router))
            {
                ReportAccessFailure("could not bind the drag-source router");
                return;
            }

            if (!reportedInstall)
            {
                reportedInstall = true;
                AppStartup.LogDiagnostic(
                    $"drag-source router installed (desktopSource={original is not null}, embeddedSource={embedded is not null})");
            }
        }
    }

    /// <summary>
    /// 登记一个嵌入面板的 Avalonia 顶层（连同 <see cref="EnsureInstalled"/>）。

    /// 面板实现方在预览控件挂载后调用；<paramref name="topLevel"/> 关闭时自动移除。

    /// </summary>
    public static void RegisterEmbeddedTopLevel(AvTopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        EnsureInstalled();
        EmbeddedTopLevels[topLevel] = 0;
        topLevel.Closed += (_, _) => EmbeddedTopLevels.TryRemove(topLevel, out _);
    }

    /// <summary>仅供诊断/测试：恢复初始 Win32 绑定。</summary>
    public static void RestoreOriginal()
    {
        lock (Gate)
        {
            if (original is null) return;
            if (!LocatorBridge.BindConstant(original))
                ReportAccessFailure("could not restore the original drag source");
            router = null;
        }
    }

    private static bool IsEmbedded(AvTopLevel? topLevel)
    {
        if (topLevel is null) return false;
        if (EmbeddedTopLevels.ContainsKey(topLevel)) return true;
        return string.Equals(topLevel.GetType().FullName, EmbeddedRootTypeName, StringComparison.Ordinal);
    }

    private static AvIPlatformDragSource? CreateRouter()
    {
        try
        {
            // 运行时接口只有一个 DoDragDropAsync；引用程序集的私有限制不影响 DispatchProxy。

            var proxy = DispatchProxy.Create<AvIPlatformDragSource, DragSourceRoutingProxy>();
            var routing = (DragSourceRoutingProxy)(object)proxy;
            routing.IsEmbeddedTopLevel = IsEmbedded;
            routing.GetDesktopSource = () => original ?? embedded;
            routing.GetEmbeddedSource = () => embedded;
            return proxy;
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"drag-source proxy creation failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void ReportAccessFailure(string message)
    {
        if (reportedAccessFailure) return;
        reportedAccessFailure = true;
        AppStartup.LogDiagnostic(
            $"drag-source routing is inactive ({message}); desktop widget drags fall back to whatever Avalonia has bound.");
    }

    /// <summary>
    /// 引用程序集把 AvaloniaLocator 的公开成员藏掉了，但运行时仍是普通公开 API。

    /// 只在这一处反射，失败时记录一次并让拖放退回 Avalonia 当前绑定。

    /// </summary>
    private static class LocatorBridge
    {
        private static readonly Type? LocatorType =
            typeof(AvIPlatformDragSource).Assembly.GetType("Avalonia.AvaloniaLocator");

        private static readonly PropertyInfo? CurrentProperty =
            LocatorType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);

        private static readonly PropertyInfo? CurrentMutableProperty =
            LocatorType?.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static);

        private static readonly MethodInfo? GetServiceMethod = LocatorType?.GetMethod(
            "GetService", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);

        private static readonly MethodInfo? BindMethod = LocatorType?.GetMethod(
            "Bind", BindingFlags.Public | BindingFlags.Instance);

        public static AvIPlatformDragSource? GetDragSource()
        {
            try
            {
                var current = CurrentProperty?.GetValue(null);
                if (current is null || GetServiceMethod is null) return null;

                var value = GetServiceMethod.Invoke(current, new object?[] { typeof(AvIPlatformDragSource) });
                return value as AvIPlatformDragSource;
            }
            catch (Exception ex)
            {
                AppStartup.LogDiagnostic($"drag-source lookup failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static bool BindConstant(AvIPlatformDragSource source)
        {
            try
            {
                var mutable = CurrentMutableProperty?.GetValue(null);
                if (mutable is null || BindMethod is null) return false;

                var helper = BindMethod.MakeGenericMethod(typeof(AvIPlatformDragSource)).Invoke(mutable, null);
                if (helper is null) return false;

                var toConstant = helper.GetType().GetMethod(
                    "ToConstant", BindingFlags.Public | BindingFlags.Instance);
                if (toConstant is null) return false;

                toConstant.MakeGenericMethod(typeof(AvIPlatformDragSource))
                    .Invoke(helper, new object?[] { source });
                return true;
            }
            catch (Exception ex)
            {
                AppStartup.LogDiagnostic($"drag-source bind failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}

/// <summary>
/// <see cref="DispatchProxy"/> 的运行时转发器：按拖放来源选择集成包服务或原 Win32 服务。
/// 由 <see cref="DragSourceRouter"/> 创建；构造函数保持公开无参，DispatchProxy 会在运行时
/// 生成一个派生类型（Avalonia 的引用程序集不允许直接实现该接口）。
/// </summary>
public class DragSourceRoutingProxy : DispatchProxy
{
    internal Func<AvTopLevel?, bool>? IsEmbeddedTopLevel { get; set; }

    internal Func<AvIPlatformDragSource?>? GetDesktopSource { get; set; }

    internal Func<AvIPlatformDragSource?>? GetEmbeddedSource { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "DoDragDropAsync" || args is null || args.Length != 3)
            return null;

        var triggerEvent = (AvPointerPressedEventArgs)args[0]!;
        var dataTransfer = (AvIDataTransfer)args[1]!;
        var allowedEffects = (AvDragDropEffects)args[2]!;
        return RouteAsync(triggerEvent, dataTransfer, allowedEffects);
    }

    private async Task<AvDragDropEffects> RouteAsync(
        AvPointerPressedEventArgs triggerEvent,
        AvIDataTransfer dataTransfer,
        AvDragDropEffects allowedEffects)
    {
        var topLevel = AvTopLevel.GetTopLevel(triggerEvent.Source as AvVisual);

        if (IsEmbeddedTopLevel?.Invoke(topLevel) == true)
        {
            var source = GetEmbeddedSource?.Invoke();
            if (source is null)
            {
                AppStartup.LogDiagnostic("embedded drag source is not available; drop ignored.");
                return AvDragDropEffects.None;
            }

            return await InvokeDoDragDropAsync(source, triggerEvent, dataTransfer, allowedEffects);
        }

        // 普通桌面组件：回到本进程启动时的 Win32 实现。若当初保存失败（极端时序），
        // 退回集成包服务也好过让拖放完全失效。

        var desktopSource = GetDesktopSource?.Invoke();
        if (desktopSource is null)
        {
            AppStartup.LogDiagnostic("no platform drag source is available; drop ignored.");
            return AvDragDropEffects.None;
        }

        return await InvokeDoDragDropAsync(desktopSource, triggerEvent, dataTransfer, allowedEffects);
    }

    /// <summary>
    /// 接口方法本身也是 PrivateApi，引用程序集看不见；运行时按名字调用真实实现。

    /// 拖放是低频操作，反射开销可以忽略。

    /// </summary>
    private static Task<AvDragDropEffects> InvokeDoDragDropAsync(
        AvIPlatformDragSource source,
        AvPointerPressedEventArgs triggerEvent,
        AvIDataTransfer dataTransfer,
        AvDragDropEffects allowedEffects)
    {
        try
        {
            var method = source.GetType().GetMethod(
                             "DoDragDropAsync", BindingFlags.Public | BindingFlags.Instance)
                         ?? typeof(AvIPlatformDragSource).GetMethod(
                             "DoDragDropAsync", BindingFlags.Public | BindingFlags.Instance);

            if (method is null)
            {
                AppStartup.LogDiagnostic("drag-source interface method not found; drop ignored.");
                return Task.FromResult(AvDragDropEffects.None);
            }

            return method.Invoke(source, new object?[] { triggerEvent, dataTransfer, allowedEffects })
                   as Task<AvDragDropEffects>
                   ?? Task.FromResult(AvDragDropEffects.None);
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"drag operation failed: {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(AvDragDropEffects.None);
        }
    }
}
