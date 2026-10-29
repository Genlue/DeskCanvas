using System;
using System.IO;
using DeskCanvas.Core;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 新入口在最早期失败时的崩溃记录。位置与旧入口保持一致（数据目录的 crash_log.txt），
/// 方便用户按现有说明把日志贴出来；写日志本身失败时不能掩盖原始异常。
/// </summary>
internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Const.DataFolder);
            File.WriteAllText(Path.Combine(Const.DataFolder, "crash_log.txt"), exception.ToString());
        }
        catch
        {
            // 写崩溃日志失败时保留原异常继续上抛。

        }
    }
}
