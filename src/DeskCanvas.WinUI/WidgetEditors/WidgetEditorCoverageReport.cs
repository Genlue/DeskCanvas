using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskCanvas.Services;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// <c>--check-editors[=&lt;path&gt;]</c>：组件编辑页覆盖率诊断（报告 §3 第三。S3.2/S3.4）。/// <para>
/// 枚举内置组件声明过的全部 <c>EditModelViewType</c>（当。37 个不同设置页），逐个。/// <see cref="WidgetEditorRegistry"/> 是否已有 WinUI 页面，输出。7 页里已实。N 页、未迁移 37-N 页”。/// 这是刻意不报“全部命中”的检查：注册了但不在权威目录里的键、反射失败的组件都会单独列出来。/// </para>
/// <para>
/// 。<c>--dump-widgets</c> 同族：只读、可重复运行，不创建窗口。带路径时把同样的内容写。JSON。/// 不带路径时只写诊断日志。b>不是</b>正常启动路径的一部分。/// </para>
/// </summary>
internal static class WidgetEditorCoverageReport
{
    private const string Prefix = "--check-editors=";
    private const string Flag = "--check-editors";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>命令行里没有 <c>--check-editors</c> 时什么都不做。/summary>
    public static void TryReportFromCommandLine()
    {
        var requested = GetRequestedPath();
        if (!requested.Requested) return;

        try
        {
            var coverage = WidgetEditorRegistry.DescribeCoverage();

            AppStartup.LogDiagnostic("--check-editors: " + coverage.Summary);
            foreach (var entry in coverage.Entries
                         .OrderBy(entry => entry.State)
                         .ThenBy(entry => entry.Key.AssemblyName, StringComparer.Ordinal)
                         .ThenBy(entry => entry.Key.ViewTypeName, StringComparer.Ordinal))
            {
                AppStartup.LogDiagnostic(
                    $"--check-editors:   [{entry.State}] {entry.Key} " +
                    $"(widgets: {string.Join(", ", entry.WidgetTypes)})");
            }

            foreach (var stale in coverage.StaleRegistrations)
            {
                AppStartup.LogDiagnostic($"--check-editors:   [Stale registration] {stale} is not a built-in edit page");
            }

            if (requested.Path is { Length: > 0 } path)
            {
                Write(path, coverage);
            }
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"--check-editors: failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Write(string path, WidgetEditorCoverage coverage)
    {
        var document = new
        {
            Schema = 1,
            GeneratedAt = DateTimeOffset.Now.ToString("O"),
            ExePath = Environment.ProcessPath,
            Total = coverage.Total,
            Migrated = coverage.Migrated,
            NotMigrated = coverage.NotMigrated,
            PluginNeedsAdapter = coverage.PluginNeedsAdapter,
            Summary = coverage.Summary,
            Errors = coverage.Errors,
            StaleRegistrations = coverage.StaleRegistrations.Select(key => key.ToString()).ToList(),
            Pages = coverage.Entries
                .OrderBy(entry => entry.Key.AssemblyName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.ViewTypeName, StringComparer.Ordinal)
                .Select(entry => new
                {
                    Assembly = entry.Key.AssemblyName,
                    ViewTypeName = entry.Key.ViewTypeName,
                    State = entry.State.ToString(),
                    WidgetTypes = entry.WidgetTypes,
                })
                .ToList(),
        };

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, Options) + Environment.NewLine);
        AppStartup.LogDiagnostic($"--check-editors: wrote coverage to {fullPath}");
    }

    private static (bool Requested, string? Path) GetRequestedPath()
    {
        var args = Program.StartupArgs is { Length: > 0 } startup
            ? startup
            : Environment.GetCommandLineArgs().Skip(1).ToArray();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[Prefix.Length..].Trim().Trim('"');
                return (true, value.Length == 0 ? null : value);
            }

            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return (true, args[i + 1].Trim().Trim('"'));
                }

                return (true, null);
            }
        }

        return (false, null);
    }
}
