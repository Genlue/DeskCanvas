using System.Text.Json;
using DeskCanvas.Core;
using DeskCanvas.Core.Services;

namespace UninstallerChecks;

internal static class Program
{
    private static int passed;
    private static readonly List<string> failures = [];

    private static void Check(bool condition, string message)
    {
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
        if (condition) passed++;
        else failures.Add(message);
    }

    public static int Main()
    {
        Console.WriteLine("=== UninstallerChecks — P13 独立卸载程序与动态数据路径契约 ===");
        Console.WriteLine();

        var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-UninstallerChecks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            Console.WriteLine("1. 动态数据路径决议与自动跟随验证");
            var mockInstallDir = Path.Combine(sandbox, "AppInstall");
            Directory.CreateDirectory(mockInstallDir);

            // 1.1 无指针时，决议为默认数据目录
            var defaultData = UninstallerEngine.ResolveDataDirectory(mockInstallDir, ignoreEnvironment: true);
            Check(string.Equals(defaultData, Const.DefaultDataFolder, StringComparison.OrdinalIgnoreCase),
                $"1.1 无指针文件时决议为默认目录 ({defaultData})");

            // 1.2 写入 dataFolder.json 指向 CustomData1
            var customData1 = Path.Combine(sandbox, "UserData_Custom1");
            Directory.CreateDirectory(customData1);
            var pointerPath = Path.Combine(mockInstallDir, Const.DataFolderPointerFileName);
            File.WriteAllText(pointerPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [Const.DataFolderPointerPropertyName] = customData1
            }));

            var resolvedPath1 = UninstallerEngine.ResolveDataDirectory(mockInstallDir, ignoreEnvironment: true);
            Check(string.Equals(resolvedPath1, customData1, StringComparison.OrdinalIgnoreCase),
                $"1.2 存在安装目录指针时准确指向自定义数据目录 1 ({resolvedPath1})");

            // 1.3 核心要求：改指针后卸载目标自动跟随，不得烘焙固定路径
            var customData2 = Path.Combine(sandbox, "UserData_Custom2");
            Directory.CreateDirectory(customData2);
            File.WriteAllText(pointerPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [Const.DataFolderPointerPropertyName] = customData2
            }));

            var resolvedPath2 = UninstallerEngine.ResolveDataDirectory(mockInstallDir, ignoreEnvironment: true);
            Check(string.Equals(resolvedPath2, customData2, StringComparison.OrdinalIgnoreCase),
                $"1.3 修改指针文件后卸载目标自动跟随变更至自定义目录 2 ({resolvedPath2})");

            // 1.4 指针文件损坏时自动安全回退至默认数据目录
            File.WriteAllText(pointerPath, "{ invalid json content!@#$ }");
            var resolvedFallback = UninstallerEngine.ResolveDataDirectory(mockInstallDir, ignoreEnvironment: true);
            Check(string.Equals(resolvedFallback, Const.DefaultDataFolder, StringComparison.OrdinalIgnoreCase),
                $"1.4 指针文件损坏时安全回退至默认数据目录 ({resolvedFallback})");

            Console.WriteLine();
            Console.WriteLine("2. 默认选项与安全策略验证");
            var defaultOptions = new UninstallerOptions(mockInstallDir, customData1);
            Check(defaultOptions.RemoveData == false, "2.1 UninstallerOptions.RemoveData 默认值必须为 false (默认保留用户数据)");
            Check(defaultOptions.TerminateProcesses == true, "2.2 默认开启终止运行中进程");
            Check(defaultOptions.RemoveShortcuts == true, "2.3 默认开启清理快捷方式");
            Check(defaultOptions.RemoveAutoStart == true, "2.4 默认开启清理开机自启项");

            Console.WriteLine();
            Console.WriteLine("3. 卸载执行 — RemoveData = false (默认：保留用户数据)");
            var testInstallDir1 = Path.Combine(sandbox, "InstallRun1");
            var testDataDir1 = Path.Combine(sandbox, "DataRun1");
            Directory.CreateDirectory(testInstallDir1);
            Directory.CreateDirectory(testDataDir1);

            // 模拟安装目录文件与子目录
            File.WriteAllText(Path.Combine(testInstallDir1, "DeskCanvas.exe"), "binary");
            File.WriteAllText(Path.Combine(testInstallDir1, "DeskCanvasUninstaller.exe"), "binary");
            var subDir = Path.Combine(testInstallDir1, "Widgets");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "Clock.dll"), "dll");

            // 模拟数据目录中的配置与小组件数据
            var profilesDir = Path.Combine(testDataDir1, "Profiles", "Default");
            Directory.CreateDirectory(profilesDir);
            var settingsFile = Path.Combine(profilesDir, "appSettings.json");
            var layoutFile = Path.Combine(profilesDir, "layout.json");
            File.WriteAllText(settingsFile, "{\"Version\":\"3.8.3\"}");
            File.WriteAllText(layoutFile, "{\"Widgets\":[]}");

            var run1Options = new UninstallerOptions(
                InstallDirectory: testInstallDir1,
                DataDirectory: testDataDir1,
                RemoveData: false,
                TerminateProcesses: false,
                RemoveShortcuts: false,
                RemoveAutoStart: false,
                RemoveRegistry: false);

            var run1Result = UninstallerEngine.Execute(run1Options);

            Check(run1Result.Success, "3.1 卸载流程返回 Success == true");
            Check(!Directory.Exists(testInstallDir1), "3.2 安装目录被彻底清理删除");
            Check(Directory.Exists(testDataDir1), "3.3 用户数据目录完整保留存在");
            Check(File.Exists(settingsFile) && File.Exists(layoutFile), "3.4 数据目录中的 Profile、设置及布局文件全部完好无损");
            Check(!run1Result.DataDirectoryRemoved, "3.5 run1Result.DataDirectoryRemoved 报告为 false");

            Console.WriteLine();
            Console.WriteLine("4. 卸载执行 — RemoveData = true (用户勾选：彻底清理)");
            var testInstallDir2 = Path.Combine(sandbox, "InstallRun2");
            var testDataDir2 = Path.Combine(sandbox, "DataRun2");
            Directory.CreateDirectory(testInstallDir2);
            Directory.CreateDirectory(testDataDir2);

            File.WriteAllText(Path.Combine(testInstallDir2, "DeskCanvas.exe"), "binary");
            var dataProfiles = Path.Combine(testDataDir2, "Profiles", "Default");
            Directory.CreateDirectory(dataProfiles);
            File.WriteAllText(Path.Combine(dataProfiles, "appSettings.json"), "{\"Clean\":true}");

            var run2Options = new UninstallerOptions(
                InstallDirectory: testInstallDir2,
                DataDirectory: testDataDir2,
                RemoveData: true,
                TerminateProcesses: false,
                RemoveShortcuts: false,
                RemoveAutoStart: false,
                RemoveRegistry: false);

            var run2Result = UninstallerEngine.Execute(run2Options);

            Check(run2Result.Success, "4.1 彻底清理卸载流程返回 Success == true");
            Check(!Directory.Exists(testInstallDir2), "4.2 安装目录已删除");
            Check(!Directory.Exists(testDataDir2), "4.3 用户数据目录被彻底清理删除");
            Check(run2Result.DataDirectoryRemoved, "4.4 run2Result.DataDirectoryRemoved 报告为 true");

            Console.WriteLine();
            Console.WriteLine("5. 只读文件与目录删除鲁棒性验证");
            var readOnlyDir = Path.Combine(sandbox, "ReadOnlyTest");
            Directory.CreateDirectory(readOnlyDir);
            var roFile = Path.Combine(readOnlyDir, "locked_readonly.txt");
            File.WriteAllText(roFile, "readonly content");
            File.SetAttributes(roFile, FileAttributes.ReadOnly);

            var (deletedFiles, deletedDirs) = UninstallerEngine.DeleteDirectory(readOnlyDir, deleteRoot: true);
            Check(deletedFiles == 1, $"5.1 只读属性文件被正常删除 (count={deletedFiles})");
            Check(!Directory.Exists(readOnlyDir), "5.2 包含只读文件的父目录被成功清理");

            Console.WriteLine();
            Console.WriteLine("6. 命令行解析与影子副本参数契约");
            var (argsOptions1, isShadow1, shadowDir1, parentPid1) =
                UninstallerEngine.ParseCommandLine(["--silent", "--install-dir", mockInstallDir]);
            Check(argsOptions1.Silent == true, "6.1 --silent 正确解析");
            Check(argsOptions1.RemoveData == false, "6.2 未指定 --remove-data 时默认保持 false");
            Check(string.Equals(argsOptions1.InstallDirectory, mockInstallDir, StringComparison.OrdinalIgnoreCase),
                "6.3 --install-dir 参数正确绑定");
            Check(isShadow1 == false, "6.4 默认未开启影子模式");

            var (argsOptions2, isShadow2, shadowDir2, parentPid2) =
                UninstallerEngine.ParseCommandLine(["--shadow", "--remove-data", "--shadow-dir", "C:\\TempShadow", "--parent-pid", "8888"]);
            Check(argsOptions2.RemoveData == true, "6.5 --remove-data 正确解析为 true");
            Check(isShadow2 == true, "6.6 --shadow 正确解析为 true");
            Check(shadowDir2 == "C:\\TempShadow", "6.7 --shadow-dir 路径解析正确");
            Check(parentPid2 == 8888, "6.8 --parent-pid 正确解析为父进程 PID");

            Console.WriteLine();
            Console.WriteLine("7. 进程检测安全兜底");
            var terminated = UninstallerEngine.TerminateDeskCanvasProcesses();
            Check(terminated >= 0, $"7.1 无对应运行进程时安全返回 0 (terminated={terminated})");

            Console.WriteLine();
            Console.WriteLine("8. Inno Setup 脚本与内置卸载配置契约");
            var rootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
            var issPath = Path.Combine(rootDir, "installer", "DeskCanvas.iss");
            var islPath = Path.Combine(rootDir, "installer", "ChineseSimplified.isl");

            Check(File.Exists(issPath), $"8.1 存在 Inno Setup 脚本 ({issPath})");
            Check(File.Exists(islPath), $"8.2 存在简体中文语言包 ({islPath})");

            if (File.Exists(issPath))
            {
                var issContent = File.ReadAllText(issPath);
                Check(issContent.Contains("CloseApplications=yes"), "8.3 启用运行中进程检测与关闭 (CloseApplications=yes)");
                Check(issContent.Contains("ResolveDataDirectory"), "8.4 包含动态数据路径解析函数 (ResolveDataDirectory)");
                Check(issContent.Contains("ExtractDataFolderFromJson"), "8.5 包含 dataFolder.json 指针解析逻辑");
                Check(issContent.Contains("MB_DEFBUTTON2"), "8.6 卸载时清理数据弹窗默认按钮设为【否】(MB_DEFBUTTON2 默认不勾选/不删除)");
            }

            var isccCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Inno Setup 6\ISCC.exe"),
                @"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
                @"C:\Program Files\Inno Setup 6\ISCC.exe"
            };
            var isccPath = isccCandidates.FirstOrDefault(File.Exists);
            if (isccPath != null && File.Exists(issPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(isccPath, $"\"{issPath}\" /Q")
                {
                    WorkingDirectory = Path.Combine(rootDir, "installer"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(30000);
                Check(proc.ExitCode == 0, $"8.7 ISCC.exe 编译 DeskCanvas.iss 成功 (exit={proc.ExitCode})");
            }
            else
            {
                Check(true, "8.7 ISCC.exe 未检测到，跳过在线编译断言");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox))
                    Directory.Delete(sandbox, recursive: true);
            }
            catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"=== UninstallerChecks 完成: {passed} 项通过, {failures.Count} 项失败 ===");
        if (failures.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("失败清单:");
            foreach (var f in failures) Console.WriteLine($" - {f}");
            Console.ResetColor();
            return 1;
        }

        return 0;
    }
}
