#requires -Version 5.1
<##
.SYNOPSIS
    串行化构建 WinUI 入口（多个并行执行者共享同一个 obj/bin，必须互斥）。
.DESCRIPTION
    多个子代理会同时改 src/DeskCanvas.WinUI 并各自构建。MSBuild 并发写同一个 obj/ 会
    随机报“文件被占用”，且 WinUI 的 XAML 编译阶段对中间产物非常敏感。本脚本用独占锁
    文件把构建排队，所有执行者都通过它构建，禁止直接跑 dotnet build。

    编译错误可能来自别人正在写的文件：只处理你自己负责路径下的错误，
    不要在别人的文件里“顺手修复”，更不要删除别人的文件。
.PARAMETER Configuration
    Debug（默认）或 Release。
.PARAMETER Project
    默认构建 WinUI 入口；也可传入其他工程。
.PARAMETER ExtraArguments
    透传给 dotnet build 的额外参数（例如 -p:Platform=x64）。
##>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Project = 'src/DeskCanvas.WinUI/DeskCanvas.WinUI.csproj',
    [string[]]$ExtraArguments = @(),
    [int]$LockTimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $root 'src/DeskCanvas.sln'))) {
    throw "repo root not found from $PSScriptRoot"
}

$lockDir = Join-Path $root 'artifacts/build-lock'
New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
$lockPath = Join-Path $lockDir 'winui-build.lock'

$deadline = (Get-Date).AddSeconds($LockTimeoutSeconds)
$handle = $null
while (-not $handle) {
    try {
        $handle = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    }
    catch {
        if ((Get-Date) -gt $deadline) { throw "winui-build.ps1: 等锁超过 $LockTimeoutSeconds 秒" }
        Write-Output '  (等待其他执行者的构建完成…)'
        Start-Sleep -Seconds 5
    }
}

try {
    Push-Location $root
    try {
        $args = @('build', $Project, '-c', $Configuration, '-v:m', '--nologo') + $ExtraArguments
        Write-Output ('$ dotnet ' + ($args -join ' '))
        & dotnet @args
        exit $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($handle) { $handle.Dispose() }
}
