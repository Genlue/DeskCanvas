using System;
using System.Collections.Generic;
using System.Linq;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using Microsoft.UI.Xaml;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// WinUI 组件编辑窗口的打开/复用/关闭（报。§3 第三。S3.1/S3.3）。/// <para>
/// 调用链：<c>Widget.EditWidget()</c> 。<see cref="ISoftwareUi.OpenWidgetEditor"/> 。/// <see cref="WinUiSoftwareUi"/>（Host）→ 本类。本类只做三件事。/// </para>
/// <list type="number">
/// <item><description><b>重新确认目标</b>：通过 DI 里的 <see cref="WidgetFactory"/> 。InstanceId
/// 拿到存活组件自己。<see cref="IWidgetLayoutProvider"/>，并核对 Generation/类型。/// 失效 。<see cref="WidgetEditorOpenResult.TargetMissing"/>（绝不写回旧对象，报。§4.2）。/description></item>
/// <item><description><b>解析编辑。/b>。see cref="WidgetEditorRegistry"/> 。/// <c>(assembly, settingsViewFullName)</c> 给出页面工厂；未迁移/插件需适配交给说明页。/description></item>
/// <item><description><b>窗口单例</b>：同一个组件已开编辑窗口时激活它，不叠第二个写入口。/description></item>
/// </list>
/// </summary>
internal static class WidgetEditorHost
{
    // 键含 Generation：同一。InstanceId 被重建（方案切换/重载组件）后，旧窗口不能继续编辑
    // 。Provider，必须为新一代开一个新窗口（报。§4.2：不允许旧回调写回失效对象）。
    private static readonly Dictionary<(Guid InstanceId, long Generation, string ViewKey), EditWidgetWindow> OpenWindows = [];

    /// <summary>打开（或激活）编辑窗口。必须在 WinUI UI 线程调用。c>WinUiSoftwareUi</c> 已切线程）。/summary>
    public static WidgetEditorOpenResult Open(IServiceProvider services, WidgetEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SettingsViewKey) || string.IsNullOrWhiteSpace(request.SettingsViewTypeName))
        {
            AppStartup.LogDiagnostic(
                $"editor request for {request.AssemblyName}/{request.SubType} has no settings view identity; ignored.");
            return WidgetEditorOpenResult.Failed;
        }

        var factory = services.GetService(typeof(WidgetFactory)) as WidgetFactory;
        var layoutProvider = factory?.TryGetLiveLayoutProvider(request.InstanceId, request.AssemblyName, request.SubType);
        if (layoutProvider is null)
        {
            AppStartup.LogDiagnostic(
                $"editor request for {request.AssemblyName}/{request.SubType} ({request.InstanceId}) was refused: " +
                "the widget is no longer alive (target missing).");
            return WidgetEditorOpenResult.TargetMissing;
        }

        var layout = layoutProvider.Get();
        if (layout is null
            || !string.Equals(layout.Type, request.AssemblyName, StringComparison.Ordinal)
            || !string.Equals(layout.SubType, request.SubType, StringComparison.Ordinal)
            || (request.InstanceId != Guid.Empty && layout.InstanceId != request.InstanceId)
            || layout.Generation != request.Generation)
        {
            AppStartup.LogDiagnostic(
                $"editor request for {request.AssemblyName}/{request.SubType} ({request.InstanceId}) was refused: " +
                $"the live layout no longer matches (generation {layout?.Generation} vs {request.Generation}).");
            return WidgetEditorOpenResult.TargetMissing;
        }

        var windowKey = (layout.InstanceId, layout.Generation, request.SettingsViewKey);
        if (OpenWindows.TryGetValue(windowKey, out var existing))
        {
            existing.BringToFront();
            AppStartup.LogDiagnostic(
                $"widget editor for {request.AssemblyName}/{request.SubType} is already open; activated the existing window.");
            return WidgetEditorOpenResult.Started;
        }

        var key = new WidgetEditorKey(
            request.SettingsViewAssemblyName ?? request.AssemblyName,
            request.SettingsViewTypeName);
        var registration = WidgetEditorRegistry.Resolve(key);

        var window = new EditWidgetWindow(services, layoutProvider, request, registration);
        window.ClosedForGood += (_, _) =>
        {
            if (OpenWindows.TryGetValue(windowKey, out var tracked) && ReferenceEquals(tracked, window))
            {
                OpenWindows.Remove(windowKey);
            }
        };

        OpenWindows[windowKey] = window;
        window.Activate();

        AppStartup.LogDiagnostic(
            $"widget editor opened for {request.AssemblyName}/{request.SubType} " +
            $"(page {key}, state {registration.State}, geometry key '{request.SettingsViewKey}', " +
            $"generation {layout.Generation}).");

        return registration.State switch
        {
            WidgetEditorMigrationState.Migrated => WidgetEditorOpenResult.Started,
            WidgetEditorMigrationState.NotMigrated => WidgetEditorOpenResult.NotMigrated,
            _ => WidgetEditorOpenResult.PluginNeedsAdapter,
        };
    }

    /// <summary>退出流程：先关编辑窗口（会保存几何。Detach 页面），再让协调器结束消息循环。/summary>
    public static void CloseAllForShutdown()
    {
        foreach (var (key, window) in OpenWindows.ToList())
        {
            OpenWindows.Remove(key);
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                AppStartup.LogDiagnostic(
                    $"closing the widget editor during shutdown failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
