#requires -Version 5.1
<##
.SYNOPSIS
    启动新 WinUI 入口（开发版）。
.DESCRIPTION
    默认使用隔离的开发数据目录（%LocalAppData%\DeskCanvas-dev）和独立的单实例作用域，
    因此可以和已安装的成品版同时开着，也不会读到/改到你的真实配置。
.PARAMETER Live
    使用真实数据目录与正式单实例作用域（等同于成品版行为）。
    此时如果已安装的 DeskCanvas 正在运行，本次启动会把手交给它然后立即退出。
.PARAMETER Settings
    启动后直接打开设置窗口。可跟页面键：General / Appearance / Spotlight / MultiScreen /
    Advanced / Profiles / About / Gallery。
.PARAMETER Rebuild
    先构建（pwsh scripts\dev\winui-build.ps1 -Configuration Release 之外的 Debug），失败则不启动。
.PARAMETER Arguments
    透传给 exe 的其他参数，例如 '--edit-widget=Clock/Digital'。
##>
[CmdletBinding()]
param(
    [switch]$Live,
    [string]$Settings,
    [switch]$Rebuild,
    [string[]]$Arguments = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $root 'src/DeskCanvas.WinUI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DeskCanvas.WinUI.exe'

if ($Rebuild) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'winui-build.ps1') -Configuration Debug
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

if (-not (Test-Path $exe)) {
    throw "未找到 $exe —— 先执行: pwsh scripts\dev\winui-build.ps1"
}

$launchArgs = @()
if ($Settings) { $launchArgs += "--settings=$Settings" }
elseif ($PSBoundParameters.ContainsKey('Settings')) { $launchArgs += '--settings' }
$launchArgs += $Arguments

if ($Live) {
    Remove-Item Env:DESKCANVAS_DATA_FOLDER -ErrorAction SilentlyContinue
    Remove-Item Env:DESKCANVAS_INSTANCE_SCOPE -ErrorAction SilentlyContinue
    $running = @(Get-Process -Name 'DeskCanvas' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Warning '已安装的 DeskCanvas 正在运行：本次启动只会把请求交给它，然后自己退出。'
    }
}
else {
    $devData = Join-Path $env:LOCALAPPDATA 'DeskCanvas-dev'
    New-Item -ItemType Directory -Force -Path $devData | Out-Null
    $env:DESKCANVAS_DATA_FOLDER = $devData
    $env:DESKCANVAS_INSTANCE_SCOPE = 'Local\DeskCanvas.Dev'
    Write-Output ("开发数据目录: {0}" -f $devData)
}

Write-Output ("启动: {0} {1}" -f $exe, ($launchArgs -join ' '))
Start-Process -FilePath $exe -ArgumentList $launchArgs
