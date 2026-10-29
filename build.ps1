#requires -Version 5.1
<#
.SYNOPSIS
    One-click build & package for the DeskCanvas fork.

.DESCRIPTION
    Publishes the WinUI entry (src\DeskCanvas.WinUI) as a DIRECTORY, not a single exe
    (report §3 step 6.5): PublishSingleFile=false, PublishTrimmed=false, self-contained.
    WinUI / Windows App SDK native libraries and .pri/.xbf XAML resources cannot be
    folded into one file, and widget assemblies are loaded by reflection.

    The original Avalonia host (src\DeskCanvas) is now a library; its DeskCanvas.dll
    keeps the assembly identity that widgets reference. Only the apphost is renamed to
    DeskCanvas.exe, so the product name stays stable while the loadable apphost still
    points at DeskCanvas.WinUI.dll / .deps.json / .runtimeconfig.json.

    Output layout (dist\<Runtime>\):
        DeskCanvas.exe                     renamed apphost (loads DeskCanvas.WinUI.dll)
        DeskCanvas.WinUI.dll/.deps.json/.runtimeconfig.json
        DeskCanvas.dll                     original Avalonia host library (same identity)
        DeskCanvas.Core/Contracts/Runtime/UI.dll
        DeskCanvas.pri / DeskCanvas.WinUI.pri   WinUI resource index (framework resolves by exe name)
        Microsoft.ui.xaml.dll, Windows App SDK natives, other *.pri, *.xbf, WebView2 loader
        appSettings.json / layout.json     default resources (embedded into DeskCanvas.dll too)
        Widgets\                           portable widget layout (widget + shared/native deps; DeskCanvas.dll
                                            also keeps the embedded bundle for installed layouts)

    Portable mode (-Portable) zips the complete directory publish. The installer
    sections (-Setup/-Msi) still collect the legacy single exe: updating installer
    file trees is report §3 step 6.7 and is deliberately out of this change.

.PARAMETER Runtime
    Target runtime: win-x64 (default), win-x86, win-arm64.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Portable
    Also zip the complete directory publish (the classic portable layout).

.EXAMPLE
    ./build.ps1                       # dist\win-x64\ (directory publish, DeskCanvas.exe + deps + Widgets)
    ./build.ps1 -Runtime win-arm64    # ARM64 directory publish
    ./build.ps1 -Portable             # directory publish + portable zip
#>
param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$Portable,

    [switch]$Setup,

    [switch]$Msi,

    # Skip the final "press Enter" pause (terminals / CI).
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\DeskCanvas.WinUI\DeskCanvas.WinUI.csproj"
$hostSource = Join-Path $root "src\DeskCanvas"
$dist = Join-Path $root "dist\$Runtime"
$widgetsBundleDir = Join-Path $root "artifacts\widgets-bundle"

# Detect a one-click launch from Explorer. The Windows PowerShell verb uses "-File",
# while the PowerShell 7 verb uses "-Command ... & '...\build.ps1'". Both must pause.
$isStandaloneLauncher = [Environment]::UserInteractive `
    -and ($Host.Name -eq "ConsoleHost") `
    -and (-not $env:CI) `
    -and (-not $NoPause) `
    -and ([Environment]::CommandLine -notmatch "(?i)-NonInteractive") `
    -and ([Environment]::CommandLine -match '(?i)(-File|-Command)[^\r\n]*build\.ps1')

function Repair-DirectoryAcl([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $aclText = (& icacls $Path 2>$null) -join "`n"
    if ($aclText -match "Low Mandatory Level|\(DENY\)") {
        Write-Host "==> Repairing sandbox ACL / integrity level on $Path..." -ForegroundColor Yellow
        & icacls $Path /reset /q | Out-Null
        & icacls $Path /setintegritylevel "(OI)(CI)M" /q | Out-Null
        & icacls "$Path\*" /reset /t /c /q 2>$null | Out-Null
    }
}

# Remove the mandatory integrity label from a file or folder.
# Windows creates a process at min(token integrity level, image file label), so a lingering M
# label silently pins the exe BELOW an elevated shell (UAC-disabled sessions, elevated Explorer):
# the app then runs at Medium while the shell runs at High, UIPI refuses every PrintWindow
# against the wallpaper hosts with ERROR_ACCESS_DENIED, and the live sampler has nothing to
# capture. A cleared label restores normal behaviour — the process follows whatever launched
# it, like every other app. The anti-contamination purpose is unaffected: the repair pass above
# still fixes a Low label (which would break the launch with 0xC0000142) before this runs.
function Clear-IntegrityLabel([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    try {
        if (-not ("DcNative.Label" -as [type])) {
            Add-Type -Namespace DcNative -Name Label -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern uint SetNamedSecurityInfoW(string pObjectName, int objectType, uint securityInfo,
    System.IntPtr owner, System.IntPtr group, System.IntPtr dacl, System.IntPtr sacl);
'@
        }
        # objectType 1 = SE_FILE_OBJECT, 0x10 = LABEL_SECURITY_INFORMATION; a null label removes it.
        $ret = [DcNative.Label]::SetNamedSecurityInfoW($Path, 1, 0x10, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
        return ($ret -eq 0)
    } catch {
        return $false
    }
}

try {
    if (-not (Test-Path $project)) { throw "Project not found: $project" }

    # Ensure external sandboxes (e.g. Low Mandatory Level / DENY(DC)) haven't poisoned the repo or dist folder.
    # A Low Mandatory Level label on an .exe causes Windows CreateProcess to drop token integrity to Low,
    # failing USER32/SHELL32 initialization with 0xC0000142 (STATUS_DLL_INIT_FAILED).
    Repair-DirectoryAcl $root
    Repair-DirectoryAcl $dist

    # Stop any running DeskCanvas instances to prevent file locks during publish. Both the
    # shipped name (DeskCanvas.exe) and the development apphost (DeskCanvas.WinUI.exe, which is
    # what the WinUI project produces before the rename) can hold the output files.
    $running = @(Get-Process -Name "DeskCanvas", "DeskCanvas.WinUI" -ErrorAction SilentlyContinue)
    if ($running) {
        Write-Warning "DeskCanvas is currently running. Stopping process to overwrite output files..."
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    # 目录发布：先清空旧产物，避免上一版单文件/旧 SDK 的 DeskCanvas.WinUI.exe、原生 DLL 混进新目录。
    if (Test-Path $dist) {
        Write-Host "==> Cleaning previous publish output: $dist" -ForegroundColor Cyan
        Remove-Item -LiteralPath $dist -Recurse -Force
    }

    Write-Host "==> Publishing DeskCanvas WinUI entry ($Configuration, $Runtime, directory, self-contained)..." -ForegroundColor Cyan
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $dist `
        --nologo `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:WindowsAppSDKSelfContained=true

    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)" }

    # WinUI XAML 资源补收集：本工程未启用 MSIX tooling，dotnet publish 只把 XBF 当作
    # CopyToOutputDirectory，项目资源索引（DeskCanvas.WinUI.pri）也不在 publish 项里；
    # 结果是发布目录能生成，但 Application.LoadComponent 启动时找不到 App.xbf/资源索引。
    # 目录发布必须从本次构建输出显式复制（报告 §3 第六步 7 要求安装器收集 .pri/.xbf）。
    $winUiBinRoot = Join-Path $root "src\DeskCanvas.WinUI\bin\$Configuration"
    $appPri = @(Get-ChildItem -LiteralPath $winUiBinRoot -Recurse -Filter "DeskCanvas.WinUI.pri" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName.EndsWith([System.IO.Path]::DirectorySeparatorChar + $Runtime, [System.StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object LastWriteTime -Descending) | Select-Object -First 1
    if ($null -eq $appPri) { throw "WinUI resource index not found under $winUiBinRoot for $Runtime" }
    $winUiBuildOutput = $appPri.DirectoryName
    Copy-Item -LiteralPath $appPri.FullName -Destination (Join-Path $dist "DeskCanvas.WinUI.pri") -Force
    $xbfFiles = @(Get-ChildItem -LiteralPath $winUiBuildOutput -Recurse -Filter "*.xbf" -File)
    if ($xbfFiles.Count -eq 0) { throw "No XBF files found under $winUiBuildOutput" }
    foreach ($xbf in $xbfFiles) {
        $relative = $xbf.FullName.Substring($winUiBuildOutput.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
        $target = Join-Path $dist $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $xbf.FullName -Destination $target -Force
    }

    # 报告 §3 第六步 4：产品 exe 继续叫 DeskCanvas.exe；只重命名 publish 暂存目录里的 apphost
    # DeskCanvas.WinUI.dll/.deps.json/.runtimeconfig.json 保持原名，原 DeskCanvas.dll 不被覆盖。
    $winUiExe = Join-Path $dist "DeskCanvas.WinUI.exe"
    if (-not (Test-Path $winUiExe)) { throw "WinUI apphost not found: $winUiExe" }
    $exe = Join-Path $dist "DeskCanvas.exe"
    if (Test-Path $exe) { Remove-Item -LiteralPath $exe -Force }
    Move-Item -LiteralPath $winUiExe -Destination $exe
    if (-not (Test-Path (Join-Path $dist "DeskCanvas.WinUI.dll"))) { throw "DeskCanvas.WinUI.dll not found next to $exe" }

    # 非打包 WinUI 按 exe 文件名解析资源索引（DeskCanvas.exe -> DeskCanvas.pri）。只改 apphost 名
    # 而不提供同名 PRI，App 构造时会以 0xC000027B 退出（本次实机验证）。同时保留 TargetName 副本，
    # 便于在同一目录直接运行未改名的开发 apphost。
    Copy-Item -LiteralPath (Join-Path $dist "DeskCanvas.WinUI.pri") -Destination (Join-Path $dist "DeskCanvas.pri") -Force

    # 默认资源：publish 已经从宿主复制；这里显式保证存在（目录发布不再像单文件那样删掉它们）。
    foreach ($defaultName in @("appSettings.json", "layout.json")) {
        Copy-Item -LiteralPath (Join-Path $hostSource $defaultName) -Destination (Join-Path $dist $defaultName) -Force
    }

    # 组件包：exe 旁的 Widgets 目录是 portable/开发布局（Const.GetWidgetsFolder 优先使用）；
    # 同时保留宿主对 widgets.zip 的嵌入（Release+RID publish 会运行原构建目标），这样安装器
    # 在第六步 7 里若选择不复制 Widgets（Program Files 不可写），仍走旧的首次运行解包路径：
    # %LocalAppData%\DeskCanvas\Widgets。代价是 Release 构建会多发布一次组件。
    # 这里复用现成的 bundle 脚本，把同一份组件 DLL 平铺进 publish 目录。
    Write-Host "==> Publishing built-in widgets (flat Widgets layout)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $widgetsBundleDir -Force | Out-Null
    $widgetZip = Join-Path $widgetsBundleDir ("widgets-{0}-{1}.zip" -f $Runtime, $Configuration)
    $pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue
    $bundleShell = if ($pwshCommand) { $pwshCommand.Source } else { "powershell" }
    & $bundleShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\build-widgets-bundle.ps1") `
        -WidgetsSourceDir (Join-Path $root "src\Widgets") `
        -StageDir (Join-Path $dist "Widgets") `
        -ZipPath $widgetZip `
        -Configuration $Configuration `
        -RuntimeIdentifier $Runtime
    if ($LASTEXITCODE -ne 0) { throw "Widget bundle build failed (exit code $LASTEXITCODE)" }
    $widgetDlls = @(Get-ChildItem -LiteralPath (Join-Path $dist "Widgets") -Filter "*.dll" -File -ErrorAction SilentlyContinue)
    if ($widgetDlls.Count -eq 0) { throw "Widget publish produced no DLLs under $dist\Widgets" }

    # Clear the mandatory integrity label from the output (and from the dist folder so future
    # publishes do not re-inherit a cap). The old behaviour stamped M here — which reads as
    # harmless but caps the process at Medium: on a machine whose shell runs at High (UAC
    # disabled, an elevated shell), the app can then never PrintWindow the wallpaper hosts —
    # UIPI refuses with ERROR_ACCESS_DENIED — and live sampling dies on the static fallback.
    # Unlabelled, the exe follows the launching session's integrity level, like every other app.
    & icacls $exe /reset /q 2>$null | Out-Null
    $exeCleared = Clear-IntegrityLabel $exe
    $dirCleared = Clear-IntegrityLabel $dist
    if (-not ($exeCleared -and $dirCleared)) {
        Write-Warning "Could not clear the integrity label; falling back to an explicit Medium stamp."
        & icacls $exe /setintegritylevel M /q 2>$null | Out-Null
    }

    $exeSizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $fileCount = @(Get-ChildItem -LiteralPath $dist -Recurse -File).Count
    Write-Host "==> Directory publish: $dist" -ForegroundColor Green
    Write-Host ("    DeskCanvas.exe ({0} MB) -> DeskCanvas.WinUI.dll; Widgets: {1} DLL(s); {2} XBF file(s); {3} files total." -f $exeSizeMb, $widgetDlls.Count, $xbfFiles.Count, $fileCount)

    # --- Optional: portable zip = the complete directory publish ---
    if ($Portable) {
        $zip = Join-Path $root "dist\DeskCanvas-$Runtime-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip
        Write-Host "==> Portable zip: $zip" -ForegroundColor Green
    }

    # --- Optional: Setup installer packaging via Inno Setup ---
    if ($Setup) {
        Write-Warning "The Setup installer still copies only the legacy single exe. Collecting the full publish tree (Windows App SDK natives, .pri/.xbf, Widgets) is report §3 step 6.7 and is NOT implemented yet."
        Write-Host "==> Building Setup Installer ($Runtime)..." -ForegroundColor Cyan
        $assemblyInfo = Get-Content (Join-Path $root "src\DeskCanvas\AssemblyInfo.cs") -Raw
        $appVerMatch = [regex]::Match($assemblyInfo, 'AssemblyVersion\("([0-9\.]+)"\)')
        $appVer = if ($appVerMatch.Success) { $appVerMatch.Groups[1].Value } else { "4.2.1" }

        $arch = switch ($Runtime) {
            "win-x64" { "x64" }
            "win-x86" { "x86" }
            "win-arm64" { "arm64" }
            default { "x64" }
        }

        $isccCandidates = @(
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        $iscc = $null
        foreach ($candidate in $isccCandidates) {
            if (Test-Path $candidate) { $iscc = $candidate; break }
        }
        if (-not $iscc) {
            $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
            if ($cmd) { $iscc = $cmd.Source }
        }

        if (-not $iscc) {
            throw "Inno Setup compiler (ISCC.exe) not found. Install it via 'winget install JRSoftware.InnoSetup'."
        }

        $issPath = Join-Path $root "installer\DeskCanvas.iss"
        $installerDir = Join-Path $root "dist\installer"
        if (-not (Test-Path $installerDir)) { New-Item -ItemType Directory -Path $installerDir -Force | Out-Null }

        & $iscc $issPath "/DMyAppVersion=$appVer" "/DMyRuntime=$Runtime" "/DMyArch=$arch"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed (exit code $LASTEXITCODE)" }

        $setupExe = Join-Path $installerDir "DeskCanvas-$appVer-$arch-Setup.exe"
        Write-Host "==> Setup installer created: $setupExe ($([math]::Round((Get-Item $setupExe).Length / 1MB, 1)) MB)" -ForegroundColor Green
    }

    # --- Optional: MSI installer packaging via WiX v5 ---
    if ($Msi) {
        Write-Warning "The MSI still references only the legacy single exe. Collecting the full publish tree is report §3 step 6.7 and is NOT implemented yet."
        Write-Host "==> Building MSI Installer ($Runtime)..." -ForegroundColor Cyan
        $wxsPath = Join-Path $root "installer\Package.wxs"
        $wxsContent = Get-Content $wxsPath -Raw
        $verMatch = [regex]::Match($wxsContent, 'Version="([0-9\.]+)"')
        $wxsVer = if ($verMatch.Success) { $verMatch.Groups[1].Value } else { "1.8.1" }

        # Keep the installer's version in step with the app. The app version lives in
        # src\DeskCanvas\AssemblyInfo.cs, so reading it here means a release can never ship an MSI
        # that claims the previous version (which is exactly what happened with 1.9.3).
        $assemblyInfo = Get-Content (Join-Path $root "src\DeskCanvas\AssemblyInfo.cs") -Raw
        $appVerMatch = [regex]::Match($assemblyInfo, 'AssemblyVersion\("([0-9\.]+)"\)')
        if ($appVerMatch.Success -and $appVerMatch.Groups[1].Value -ne $wxsVer) {
            $appVer = $appVerMatch.Groups[1].Value
            Set-Content -Path $wxsPath -Value ($wxsContent -replace 'Version="[0-9\.]+"', "Version=`"$appVer`"") -NoNewline
            Write-Host "==> Synced installer version $wxsVer -> $appVer" -ForegroundColor Yellow
            $wxsVer = $appVer
        }
        $msiDir = Join-Path $root "dist\installer"
        if (-not (Test-Path $msiDir)) { New-Item -ItemType Directory -Path $msiDir -Force | Out-Null }
        $msiOut = Join-Path $msiDir "DeskCanvas-$wxsVer-$Runtime.msi"
        $arch = switch ($Runtime) {
            "win-x64" { "x64" }
            "win-x86" { "x86" }
            "win-arm64" { "arm64" }
            default { "x64" }
        }
        wix build (Join-Path $root "installer\Package.wxs") -arch $arch -ext WixToolset.UI.wixext -out $msiOut
        if ($LASTEXITCODE -ne 0) { throw "WiX build failed (exit code $LASTEXITCODE)" }
        Write-Host "==> MSI created: $msiOut ($([math]::Round((Get-Item $msiOut).Length / 1MB, 1)) MB)" -ForegroundColor Green
    }

    Write-Host "Done." -ForegroundColor Green
    if ($isStandaloneLauncher) {
        Read-Host "Build succeeded. Press Enter to close"
    }
}
catch {
    Write-Host ""
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    if ($isStandaloneLauncher) {
        Read-Host "Build failed. Press Enter to close"
    }
    exit 1
}
