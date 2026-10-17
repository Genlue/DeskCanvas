using System.Diagnostics;
using Microsoft.Win32;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Services;

/// <summary>
/// Options controlling the uninstallation execution.
/// </summary>
public sealed record UninstallerOptions(
    string InstallDirectory,
    string DataDirectory,
    bool RemoveData = false,
    bool TerminateProcesses = true,
    bool RemoveShortcuts = true,
    bool RemoveAutoStart = true,
    bool RemoveRegistry = true,
    bool Silent = false);

/// <summary>
/// Progress status reported during uninstallation.
/// </summary>
public sealed record UninstallerProgress(
    string Status,
    int Percentage,
    string? Detail = null);

/// <summary>
/// Result of the uninstallation run.
/// </summary>
public sealed record UninstallerResult(
    bool Success,
    int TerminatedProcessCount,
    int DeletedFilesCount,
    int DeletedDirectoriesCount,
    bool DataDirectoryRemoved,
    IReadOnlyList<string> Errors);

/// <summary>
/// Standalone uninstaller engine responsible for discovering dynamic paths,
/// terminating active processes, removing shortcuts, registry keys, installation files,
/// and optionally user data.
/// </summary>
public static class UninstallerEngine
{
    private const string AppName = Const.AppName;
    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryAppRoot = @"Software\Genlue\DeskCanvas";

    /// <summary>
    /// Resolves the installation directory, following explicit argument -> registry -> current directory.
    /// </summary>
    public static string ResolveInstallDirectory(string? candidate = null)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var trimmed = candidate.Trim();
            if (Directory.Exists(trimmed) || File.Exists(trimmed))
            {
                var full = Path.GetFullPath(trimmed);
                return File.Exists(full) ? Path.GetDirectoryName(full)! : full;
            }
            return Path.GetFullPath(trimmed);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var appKey = Registry.CurrentUser.OpenSubKey(RegistryAppRoot);
                var regInstallDir = appKey?.GetValue("InstallDir") as string;
                if (!string.IsNullOrWhiteSpace(regInstallDir) && Directory.Exists(regInstallDir))
                    return Path.GetFullPath(regInstallDir.Trim());

                using var appPathKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\DeskCanvas.exe");
                var pathVal = appPathKey?.GetValue("Path") as string;
                if (!string.IsNullOrWhiteSpace(pathVal) && Directory.Exists(pathVal))
                    return Path.GetFullPath(pathVal.Trim());
            }
            catch
            {
                // Registry probing is best-effort
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
                return Path.GetFullPath(dir);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    }

    /// <summary>
    /// Dynamically resolves the data folder for the given installation directory.
    /// Follows dataFolder.json pointer or default LocalAppData\DeskCanvas.
    /// </summary>
    public static string ResolveDataDirectory(string installDir, bool ignoreEnvironment = false)
    {
        var resolution = Const.ResolveDataFolder(installDir, ignoreEnvironment);
        return resolution.Folder;
    }

    /// <summary>
    /// Gracefully closes or terminates any running DeskCanvas processes.
    /// </summary>
    public static int TerminateDeskCanvasProcesses(int timeoutMs = 3000)
    {
        var currentPid = Environment.ProcessId;
        var terminated = 0;

        try
        {
            var processes = Process.GetProcessesByName(AppName)
                .Where(p => p.Id != currentPid)
                .ToList();

            if (processes.Count == 0) return 0;

            foreach (var p in processes)
            {
                try
                {
                    if (p.HasExited) continue;

                    // Polite shutdown request
                    p.CloseMainWindow();
                }
                catch
                {
                    // Ignore transient errors
                }
            }

            var stopWatch = Stopwatch.StartNew();
            var remaining = processes.Where(p => !p.HasExited).ToList();

            while (remaining.Count > 0 && stopWatch.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(100);
                remaining = remaining.Where(p => !p.HasExited).ToList();
            }

            foreach (var p in remaining)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(1000);
                    }
                    terminated++;
                }
                catch
                {
                    // Ignore process already dead
                }
            }

            terminated += (processes.Count - remaining.Count);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UninstallerEngine] Process termination warning: {ex.Message}");
        }

        return terminated;
    }

    /// <summary>
    /// Deletes all files and subdirectories within a directory, and optionally removes the root.
    /// Clears ReadOnly attributes before file deletion.
    /// </summary>
    public static (int FilesCount, int DirsCount) DeleteDirectory(string dirPath, bool deleteRoot = true, List<string>? errors = null)
    {
        var filesCount = 0;
        var dirsCount = 0;

        if (!Directory.Exists(dirPath))
            return (0, 0);

        try
        {
            var directoryInfo = new DirectoryInfo(dirPath);
            foreach (var file in directoryInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.IsReadOnly)
                        file.Attributes = FileAttributes.Normal;
                    file.Delete();
                    filesCount++;
                }
                catch (Exception ex)
                {
                    errors?.Add($"Failed to delete file '{file.FullName}': {ex.Message}");
                }
            }

            var subdirs = directoryInfo.GetDirectories("*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.FullName.Length)
                .ToList();

            foreach (var dir in subdirs)
            {
                try
                {
                    if (Directory.Exists(dir.FullName))
                    {
                        dir.Delete(recursive: false);
                        dirsCount++;
                    }
                }
                catch (Exception ex)
                {
                    errors?.Add($"Failed to delete directory '{dir.FullName}': {ex.Message}");
                }
            }

            if (deleteRoot && Directory.Exists(dirPath))
            {
                try
                {
                    Directory.Delete(dirPath, recursive: false);
                    dirsCount++;
                }
                catch (Exception ex)
                {
                    errors?.Add($"Failed to delete root directory '{dirPath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors?.Add($"Directory walk error for '{dirPath}': {ex.Message}");
        }

        return (filesCount, dirsCount);
    }

    /// <summary>
    /// Removes Start Menu and Desktop shortcuts for DeskCanvas.
    /// </summary>
    public static void RemoveShortcuts(string appName = AppName)
    {
        if (!OperatingSystem.IsWindows()) return;

        var candidateShortcutFiles = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{appName}.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), $"{appName}.lnk")
        };

        var candidateShortcutFolders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), appName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), appName)
        };

        foreach (var file in candidateShortcutFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort
            }
        }

        foreach (var folder in candidateShortcutFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                    DeleteDirectory(folder, deleteRoot: true);
            }
            catch
            {
                // Best-effort
            }
        }
    }

    /// <summary>
    /// Removes autostart entry from Windows Registry.
    /// </summary>
    public static void RemoveAutoStart(string appName = AppName)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            key?.DeleteValue(appName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort
        }
    }

    /// <summary>
    /// Removes application registry keys.
    /// </summary>
    public static void RemoveAppRegistry()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(@"Software\Genlue", writable: true);
            rootKey?.DeleteSubKeyTree(AppName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort
        }
    }

    /// <summary>
    /// Executes the full uninstallation plan according to options.
    /// </summary>
    public static UninstallerResult Execute(UninstallerOptions options, IProgress<UninstallerProgress>? progress = null)
    {
        var errors = new List<string>();
        var terminatedCount = 0;
        var totalDeletedFiles = 0;
        var totalDeletedDirs = 0;
        var dataRemoved = false;

        // Step 1: Process termination
        progress?.Report(new UninstallerProgress("正在检测并关闭正在运行的 DeskCanvas 进程...", 10));
        if (options.TerminateProcesses)
        {
            terminatedCount = TerminateDeskCanvasProcesses();
        }

        // Step 2: Shortcuts removal
        progress?.Report(new UninstallerProgress("正在清理开始菜单与桌面快捷方式...", 30));
        if (options.RemoveShortcuts)
        {
            RemoveShortcuts();
        }

        // Step 3: Registry removal
        progress?.Report(new UninstallerProgress("正在清理开机自启与注册表配置...", 45));
        if (options.RemoveAutoStart)
        {
            RemoveAutoStart();
        }
        if (options.RemoveRegistry)
        {
            RemoveAppRegistry();
        }

        // Step 4: Installation directory removal
        progress?.Report(new UninstallerProgress("正在删除程序安装文件...", 65, options.InstallDirectory));
        if (Directory.Exists(options.InstallDirectory))
        {
            var (files, dirs) = DeleteDirectory(options.InstallDirectory, deleteRoot: true, errors);
            totalDeletedFiles += files;
            totalDeletedDirs += dirs;
        }

        // Step 5: Data directory removal (Conditional)
        if (options.RemoveData)
        {
            progress?.Report(new UninstallerProgress("正在删除用户配置与小组件数据...", 85, options.DataDirectory));
            if (Directory.Exists(options.DataDirectory))
            {
                var (files, dirs) = DeleteDirectory(options.DataDirectory, deleteRoot: true, errors);
                totalDeletedFiles += files;
                totalDeletedDirs += dirs;
                dataRemoved = true;
            }

            // Also clean up anchor pointer in %LocalAppData%\DeskCanvas if data folder was redirected elsewhere
            var defaultData = Const.DefaultDataFolder;
            if (!string.Equals(options.DataDirectory, defaultData, StringComparison.OrdinalIgnoreCase) && Directory.Exists(defaultData))
            {
                DeleteDirectory(defaultData, deleteRoot: true, errors);
            }
        }
        else
        {
            progress?.Report(new UninstallerProgress("保留用户数据与配置目录...", 85, options.DataDirectory));
        }

        progress?.Report(new UninstallerProgress("卸载完成", 100));

        var success = errors.Count == 0;
        return new UninstallerResult(
            Success: success,
            TerminatedProcessCount: terminatedCount,
            DeletedFilesCount: totalDeletedFiles,
            DeletedDirectoriesCount: totalDeletedDirs,
            DataDirectoryRemoved: dataRemoved,
            Errors: errors);
    }

    /// <summary>
    /// Parses command line arguments into UninstallerOptions and shadow-copy execution flags.
    /// </summary>
    public static (UninstallerOptions Options, bool IsShadow, string? ShadowDir, int? ParentPid) ParseCommandLine(
        string[] args,
        string? defaultInstallDir = null,
        bool ignoreEnvironment = false)
    {
        var silent = false;
        var removeData = false;
        string? installDir = null;
        var isShadow = false;
        string? shadowDir = null;
        int? parentPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-s", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (string.Equals(arg, "--remove-data", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/remove-data", StringComparison.OrdinalIgnoreCase))
            {
                removeData = true;
            }
            else if (string.Equals(arg, "--shadow", StringComparison.OrdinalIgnoreCase))
            {
                isShadow = true;
            }
            else if ((string.Equals(arg, "--install-dir", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(arg, "-i", StringComparison.OrdinalIgnoreCase)) &&
                     i + 1 < args.Length)
            {
                installDir = args[++i];
            }
            else if (string.Equals(arg, "--shadow-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                shadowDir = args[++i];
            }
            else if (string.Equals(arg, "--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var pid))
                    parentPid = pid;
            }
        }

        var resolvedInstallDir = ResolveInstallDirectory(installDir ?? defaultInstallDir);
        var resolvedDataDir = ResolveDataDirectory(resolvedInstallDir, ignoreEnvironment);

        var options = new UninstallerOptions(
            InstallDirectory: resolvedInstallDir,
            DataDirectory: resolvedDataDir,
            RemoveData: removeData,
            Silent: silent);

        return (options, isShadow, shadowDir, parentPid);
    }
}
