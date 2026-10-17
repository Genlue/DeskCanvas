using System.Diagnostics;
using Avalonia;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Uninstaller;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var (options, isShadow, shadowDir, parentPid) = UninstallerEngine.ParseCommandLine(args);

        // 1. Silent mode
        if (options.Silent)
        {
            var result = UninstallerEngine.Execute(options);
            CleanupShadowDirectoryIfPresent(shadowDir);
            return result.Success ? 0 : 1;
        }

        // 2. Check if running inside the target install directory without --shadow
        var currentExe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var currentDir = Path.GetDirectoryName(currentExe)!;

        if (!isShadow)
        {
            var isUnderInstallDir = false;
            try
            {
                var fullCurrent = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullInstall = Path.GetFullPath(options.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                isUnderInstallDir = fullCurrent.Equals(fullInstall, StringComparison.OrdinalIgnoreCase) ||
                                    fullCurrent.StartsWith(fullInstall + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            if (isUnderInstallDir)
            {
                try
                {
                    var tempRoot = Path.Combine(Path.GetTempPath(), "DeskCanvasUninstaller_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tempRoot);

                    foreach (var file in Directory.GetFiles(currentDir))
                    {
                        var dest = Path.Combine(tempRoot, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                    }

                    var shadowExe = Path.Combine(tempRoot, Path.GetFileName(currentExe));
                    var childArgs = new List<string>(args)
                    {
                        "--shadow",
                        "--install-dir", options.InstallDirectory,
                        "--shadow-dir", tempRoot,
                        "--parent-pid", Environment.ProcessId.ToString()
                    };

                    var psi = new ProcessStartInfo(shadowExe, string.Join(" ", childArgs.Select(QuoteArgument)))
                    {
                        UseShellExecute = true
                    };

                    Process.Start(psi);
                    // Exit immediately so that original files inside installDir are unlocked!
                    return 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Uninstaller] Shadow-copy preparation failed, continuing in-place: {ex.Message}");
                }
            }
        }
        else
        {
            // Running as shadow copy: wait for parent process to exit
            if (parentPid.HasValue)
            {
                try
                {
                    var parent = Process.GetProcessById(parentPid.Value);
                    parent.WaitForExit(3000);
                }
                catch
                {
                    // Parent process already exited
                }
            }
        }

        // 3. Interactive GUI
        var vm = new UninstallerViewModel(options, shadowDir);
        var app = BuildAvaloniaApp(vm);
        var exitCode = app.StartWithClassicDesktopLifetime(args);

        // 4. Schedule cleanup of temp shadow directory
        CleanupShadowDirectoryIfPresent(shadowDir);

        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp(UninstallerViewModel vm) =>
        AppBuilder.Configure(() => new App(vm))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void CleanupShadowDirectoryIfPresent(string? shadowDir)
    {
        if (string.IsNullOrEmpty(shadowDir) || !Directory.Exists(shadowDir)) return;

        try
        {
            var cmd = $"/c timeout /t 2 /nobreak > NUL & rmdir /s /q \"{shadowDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch
        {
            // Background cleanup is fire-and-forget
        }
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        return arg.Contains(' ') ? $"\"{arg}\"" : arg;
    }
}
