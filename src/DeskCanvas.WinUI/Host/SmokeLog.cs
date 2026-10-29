using System;
using System.IO;
using System.Threading;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// <c>--smoke=N</c> 的阶段日志。协调者用它在无人值守环境验证混合宿主走到了哪一步：
/// 每行只写一个阶段名（appstartup / avalonia-init / di-built / widgets-shown / ui-ready / exiting），
/// 不写时间戳，避免脚本解析时还要过滤。
/// <para>
/// 路径优先取 <c>DESKCANVAS_SMOKE_LOG</c>（冒烟脚本给绝对路径），否则回退到
/// <c>%TEMP%\DeskCanvas-winui-smoke.log</c>。只追加，不截断：脚本每次运行前会删旧文件，
/// 而开发时手动多次运行保留上下文更有用。日志失败绝不拖垮启动。
/// </para>
/// </summary>
internal static class SmokeLog
{
    private static readonly object Gate = new();
    private static string? path;
    private static bool enabled;
    private static int configured;

    public static bool Enabled => enabled;

    public static void Configure(StartupOptions options)
    {
        if (Interlocked.Exchange(ref configured, 1) != 0) return;

        enabled = options.SmokeMilliseconds > 0;
        if (!enabled) return;

        try
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("DESKCANVAS_SMOKE_LOG");
            path = string.IsNullOrWhiteSpace(fromEnvironment)
                ? Path.Combine(Path.GetTempPath(), "DeskCanvas-winui-smoke.log")
                : Path.GetFullPath(fromEnvironment);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }
        catch
        {
            path = null;
        }
    }

    public static void Stage(string stage)
    {
        if (!enabled) return;

        lock (Gate)
        {
            if (path is null) return;
            try
            {
                File.AppendAllText(path, stage + Environment.NewLine);
            }
            catch
            {
                // 冒烟日志只是诊断手段，不能成为启动失败的原因。

            }
        }
    }
}
