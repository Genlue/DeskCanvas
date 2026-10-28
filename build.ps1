#requires -Version 5.1
<#
.SYNOPSIS
    One-click build & package for the DeskCanvas fork.

.DESCRIPTION
    Produces a single-file DeskCanvas.exe with all widgets embedded (widgets are
    extracted to %LocalAppData%\DeskCanvas on first run, so they stay hot-updatable).

    The exe is SELF-CONTAINED: the .NET 10 runtime travels inside it, so a target
    machine needs nothing installed. That is what the Setup installer ships — the
    framework-dependent build only ever existed for the old zip/portable releases.
    Expect ~150 MB for the raw exe (ReadyToRun + embedded runtime) and roughly half
    that for the compressed Setup.

    Portable mode (-Portable) additionally copies the Widgets folder and JSON
    settings next to the exe (classic official layout) and zips the whole thing.

.PARAMETER Runtime
    Target runtime: win-x64 (default), win-x86, win-arm64.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Portable
    Also produce the classic portable zip (exe + Widgets + json files).

.EXAMPLE
    ./build.ps1                       # dist\win-x64\DeskCanvas.exe (single file)
    ./build.ps1 -Runtime win-arm64    # ARM64 single file
    ./build.ps1 -Portable             # single file + portable zip
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
$project = Join-Path $root "src\DeskCanvas\DeskCanvas.csproj"
$dist = Join-Path $root "dist\$Runtime"

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

    # Stop any running DeskCanvas instances to prevent file lock during publish
    $running = Get-Process -Name "DeskCanvas" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Warning "DeskCanvas is currently running. Stopping process to overwrite output binary..."
        Stop-Process -Name "DeskCanvas" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    Write-Host "==> Publishing DeskCanvas ($Configuration, $Runtime, single-file)..." -ForegroundColor Cyan
    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $dist `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)" }

    # --- Strict single exe: remove stray content copied next to the exe ---
    # Each delete is guarded individually: safe-delete/trash guards on some machines throw
    # terminating errors out of Remove-Item even with -ErrorAction SilentlyContinue, and one
    # blocked file must not abort the rest of the packaging steps.
    Get-ChildItem $dist -File | Where-Object {
        $_.Extension -in ".pdb", ".xml", ".err" -or $_.Name -in "appSettings.json", "layout.json", "icon.ico", "weather_crash_log.txt"
    } | ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }
        catch { Write-Warning "Could not delete stray file $($_.FullName): $($_.Exception.Message)" }
    }

    # A leftover portable-layout Widgets folder next to the single-file exe silently flips the
    # app into portable mode (Const.GetWidgetsFolder prefers the exe-adjacent Widgets folder and
    # WidgetBundle.ExtractIfNeeded skips the embedded bundle when one exists there), so every
    # rebuild would keep hot-loading stale widget DLLs from disk instead of the fresh bundle.
    # Single-file dist must never carry one — remove it so the embedded bundle stays authoritative.
    # If a delete guard blocks the removal, a rename achieves the same effect: the folder no
    # longer sits next to the exe under the name "Widgets", so packaged mode resumes.
    $staleDistWidgets = Join-Path $dist "Widgets"
    if (Test-Path $staleDistWidgets) {
        Write-Warning "Removing stale portable-layout Widgets folder next to the exe: $staleDistWidgets"
        try {
            Remove-Item -LiteralPath $staleDistWidgets -Recurse -Force -ErrorAction Stop
        } catch {
            $staleName = "Widgets.stale-" + (Get-Date -Format "yyyyMMdd-HHmmss")
            Rename-Item -LiteralPath $staleDistWidgets -NewName $staleName -Force
            Write-Warning "Delete blocked ($($_.Exception.Message)); renamed to $staleName instead."
        }
    }

    $exe = Join-Path $dist "DeskCanvas.exe"
    if (-not (Test-Path $exe)) { throw "Single-file exe not found: $exe" }

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

    Write-Host "==> Single file: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)" -ForegroundColor Green

    # --- Optional: classic portable layout (exe + Widgets + settings), zipped ---
    if ($Portable) {
        $portableDir = Join-Path $root "dist\portable-$Runtime"
        if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
        New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

        Copy-Item $exe $portableDir
        Copy-Item (Join-Path $root "src\DeskCanvas\appSettings.json") $portableDir
        Copy-Item (Join-Path $root "src\DeskCanvas\layout.json") $portableDir

        $widgetsSrc = Join-Path $project "..\DeskCanvas\bin\$Configuration\net10.0\Widgets"
        if (-not (Test-Path $widgetsSrc)) { $widgetsSrc = Join-Path $root "src\DeskCanvas\bin\$Configuration\net10.0\Widgets" }
        if (Test-Path $widgetsSrc) {
            Copy-Item $widgetsSrc (Join-Path $portableDir "Widgets") -Recurse
        } else {
            Write-Warning "Widgets build output not found at $widgetsSrc -- portable zip will lack widgets."
        }

        $zip = Join-Path $root "dist\DeskCanvas-$Runtime-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path "$portableDir\*" -DestinationPath $zip
        Write-Host "==> Portable zip: $zip" -ForegroundColor Green
    }

    # --- Optional: Setup installer packaging via Inno Setup ---
    if ($Setup) {
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
