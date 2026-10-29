using System;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 新入口的启动开关。只解析宿主要用的参数，其余参数保持原样交给 Avalonia lifetime
/// （旧代码会读 <c>Args[0]</c> 的 <c>--settings</c> / <c>--spotlight</c>）。
/// </summary>
internal sealed class StartupOptions
{
    public const string SmokePrefix = "--smoke=";
    public const string DiagnosticFlag = "--winui-diagnostic";
    public const string SettingsPrefix = "--settings=";
    public const string EditWidgetPrefix = "--edit-widget=";
    public const string EditWidgetFlag = "--edit-widget";

    private StartupOptions(int smokeMilliseconds, bool diagnostic, string? settingsPageKey,
        bool editWidgetRequested, string? editWidgetTarget)
    {
        SmokeMilliseconds = smokeMilliseconds;
        Diagnostic = diagnostic;
        SettingsPageKey = settingsPageKey;
        EditWidgetRequested = editWidgetRequested;
        EditWidgetTarget = editWidgetTarget;
    }

    /// <summary><c>--smoke=N</c>：正常启动 N 毫秒后自动退出；0 表示不启用。</summary>
    public int SmokeMilliseconds { get; }

    /// <summary><c>--winui-diagnostic</c>：显示诊断窗口（正常启动不显示）。</summary>
    public bool Diagnostic { get; }

    /// <summary>
    /// <c>--settings=&lt;pageKey&gt;</c>：启动后直接打开设置窗口并落到指定页面
    /// （页面键见 <see cref="DeskCanvas.Services.SettingsPageKeys"/>）。冒烟与页面开发用它
    /// 直达某一页；不带等号的原 <c>--settings</c> 仍由旧逻辑处理（落在默认页）。

    /// </summary>
    public string? SettingsPageKey { get; }

    /// <summary>
    /// <c>--edit-widget[=Type/SubType]</c>：开发/冒烟专用开关——启动完成后打开第一个匹配的
    /// 可编辑组件（不带值 = 第一个可编辑组件），用来在无人值守环境验证 WinUI 编辑窗口。

    /// 这不是产品功能，正式入口只从组件右键菜单触发。

    /// </summary>
    public bool EditWidgetRequested { get; }

    /// <summary><c>Type/SubType</c>（如 <c>Clock/Digital</c>）；裸开关时为 null。</summary>
    public string? EditWidgetTarget { get; }

    public static StartupOptions Parse(string[]? args)
    {
        var smoke = 0;
        var diagnostic = false;
        string? settingsPageKey = null;
        var editWidgetRequested = false;
        string? editWidgetTarget = null;
        if (args is null) return new StartupOptions(smoke, diagnostic, settingsPageKey,
            editWidgetRequested, editWidgetTarget);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(SmokePrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg.AsSpan(SmokePrefix.Length), out var milliseconds))
            {
                smoke = Math.Max(0, milliseconds);
                continue;
            }

            // 兼容 "--smoke 8000" 的写法；脚本用的是等号形式。

            if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && int.TryParse(args[i + 1], out var separated))
            {
                smoke = Math.Max(0, separated);
                i++;
                continue;
            }

            if (arg.StartsWith(SettingsPrefix, StringComparison.OrdinalIgnoreCase)
                && arg.Length > SettingsPrefix.Length)
            {
                settingsPageKey = arg[SettingsPrefix.Length..].Trim();
                continue;
            }

            if (string.Equals(arg, DiagnosticFlag, StringComparison.OrdinalIgnoreCase))
                diagnostic = true;

            if (arg.StartsWith(EditWidgetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                editWidgetRequested = true;
                var value = arg[EditWidgetPrefix.Length..].Trim();
                editWidgetTarget = value.Length == 0 ? null : value;
                continue;
            }

            if (string.Equals(arg, EditWidgetFlag, StringComparison.OrdinalIgnoreCase))
            {
                editWidgetRequested = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    editWidgetTarget = args[i + 1].Trim();
                    i++;
                }

                continue;
            }
        }

        return new StartupOptions(smoke, diagnostic, settingsPageKey, editWidgetRequested, editWidgetTarget);
    }
}