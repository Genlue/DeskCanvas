#requires -Version 5.1
<##
.SYNOPSIS
    WinUI 入口的无人值守冒烟：隔离数据目录启动 → 截图 → 自动退出 → 检查残留。
.DESCRIPTION
    迁移期间两个东西会和机器上的成品版打架，脚本里都隔离掉：
      1. 数据目录：DESKCANVAS_DATA_FOLDER 指向 artifacts/smoke/data，绝不碰用户的
         %LocalAppData%\DeskCanvas。
      2. 单实例：DESKCANVAS_INSTANCE_SCOPE 换成冒烟专用作用域，否则正在运行的
         成品版会让开发版把自己当成第二次启动而直接退出。
    截图只抓本进程的窗口矩形，不抓整屏。
.PARAMETER Arguments
    透传给应用的参数（默认 --smoke=<Seconds*1000>）。传 --settings 可以看到设置窗口。
.PARAMETER Seconds
    应用存活时间，也是 --smoke 的毫秒来源。
##>
[CmdletBinding()]
param(
    [int]$Seconds = 10,
    [string[]]$Arguments = @(),
    [switch]$FreshData,
    [switch]$NoShot
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $root 'src/DeskCanvas.WinUI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DeskCanvas.WinUI.exe'
if (-not (Test-Path $exe)) {
    Write-Error "未找到 $exe —— 先跑 pwsh scripts/dev/winui-build.ps1" -ErrorAction Continue
    exit 2
}

$smokeRoot = Join-Path $root 'artifacts/smoke'
$dataDir = Join-Path $smokeRoot 'data'
$logFile = Join-Path $smokeRoot 'winui-smoke.log'
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
if ($FreshData -and (Test-Path $dataDir)) { Remove-Item -Recurse -Force $dataDir }
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
if (Test-Path $logFile) { Remove-Item -Force $logFile }

# 冒烟也要和构建互斥：并行构建会在运行期间替换 bin 里的程序集，进程会以莫名其妙的
# 退出码结束（A 执行者实测到过两次）。独占锁与 winui-build.ps1 是同一个文件。
$lockDir = Join-Path $root 'artifacts/build-lock'
New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
$lockPath = Join-Path $lockDir 'winui-build.lock'
$lockHandle = $null
$lockDeadline = (Get-Date).AddMinutes(20)
while (-not $lockHandle) {
    try {
        $lockHandle = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    }
    catch {
        if ((Get-Date) -gt $lockDeadline) { throw 'smoke-winui.ps1: 等待构建锁超时' }
        Write-Output '  (等待进行中的构建完成…)'
        Start-Sleep -Seconds 5
    }
}

$env:DESKCANVAS_DATA_FOLDER = $dataDir
$env:DESKCANVAS_INSTANCE_SCOPE = 'Local\DeskCanvas.SmokeTest'
$env:DESKCANVAS_SMOKE_LOG = $logFile

# 容忍 pwsh -File 传参时被整体加引号/逗号拼接的写法（'-a','-b' 会变成一个字符串）：
# 按逗号拆开、去掉首尾引号，再交给 Start-Process。
$flatArguments = @()
foreach ($rawArgument in $Arguments) {
    foreach ($piece in ($rawArgument -split ',')) {
        $clean = $piece.Trim().Trim("'").Trim('"')
        if ($clean) { $flatArguments += $clean }
    }
}
$Arguments = $flatArguments
if ($Arguments.Count -eq 0) { $Arguments = @("--smoke=$($Seconds * 1000)") }

# 清掉上次残留（同作用域的僵尸进程会让本次启动直接走「第二次启动」分支）
Get-Process -Name 'DeskCanvas.WinUI' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Output ("启动: {0} {1}" -f $exe, ($Arguments -join ' '))
$started = Get-Date
$p = Start-Process -FilePath $exe -ArgumentList $Arguments -PassThru

$shotPath = $null
if (-not $NoShot) {
    Add-Type -AssemblyName System.Drawing
    if (-not ('SmokeDpiAwareness' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SmokeDpiAwareness {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@
    }
    # PowerShell 默认不是 DPI 感知进程：在 150% 缩放下 GetWindowRect 的物理坐标会被虚拟化，
    # 截图区域比真实窗口小一圈（右/下被裁掉）。先声明 PerMonitorV2，再去量窗口。
    try { [SmokeDpiAwareness]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }
    if (-not ('SmokeWindowProbe' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SmokeWindowProbe {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
    }
    Start-Sleep -Seconds ([Math]::Min(5, [Math]::Max(2, $Seconds - 2)))
    $p.Refresh()
    $hwnd = $p.MainWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) {
        [SmokeWindowProbe]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 700
        $rect = New-Object SmokeWindowProbe+RECT
        [SmokeWindowProbe]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
        $w = $rect.Right - $rect.Left
        $h = $rect.Bottom - $rect.Top
        if ($w -gt 0 -and $h -gt 0) {
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
            $shotPath = Join-Path $smokeRoot ('shot-' + (Get-Date -Format 'HHmmss') + '.png')
            $bmp.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Output ("截图: {0} ({1}x{2})" -f $shotPath, $w, $h)
        }
    }
    else {
        Write-Output '截图: 进程在截图时刻还没有主窗口'
    }
}

$exited = $p.WaitForExit([Math]::Max(20000, ($Seconds + 25) * 1000))
$elapsed = ((Get-Date) - $started).TotalSeconds

$result = [ordered]@{
    Exe       = $exe
    Arguments = ($Arguments -join ' ')
    Exited    = $exited
    ExitCode  = if ($exited) { $p.ExitCode } else { $null }
    Seconds   = [Math]::Round($elapsed, 1)
    Shot      = $shotPath
    DataDir   = $dataDir
}

if (-not $exited) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $result.KilledAfterTimeout = $true
}

$leftovers = @(Get-Process -Name 'DeskCanvas.WinUI' -ErrorAction SilentlyContinue)
$result.LeftoverProcesses = $leftovers.Count
foreach ($leftover in $leftovers) { Stop-Process -Id $leftover.Id -Force -ErrorAction SilentlyContinue }

Write-Output ''
Write-Output '=== 冒烟结果 ==='
$result.GetEnumerator() | ForEach-Object { '{0,-20} {1}' -f $_.Key, $_.Value }

if (Test-Path $logFile) {
    Write-Output ''
    Write-Output ('=== 启动日志: ' + $logFile + ' ===')
    Get-Content -LiteralPath $logFile | ForEach-Object { $_ }
}
else {
    Write-Output ''
    Write-Output '（没有启动日志：--smoke 的日志写入还没实现，或启动在写日志之前就失败了）'
}

if ($lockHandle) { $lockHandle.Dispose() }

if (-not $exited -or $result.ExitCode -ne 0 -or $result.LeftoverProcesses -gt 0) { exit 1 }
exit 0