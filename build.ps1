#requires -Version 5.1
<#
.SYNOPSIS
    One-click build & package for the DeskCanvas fork.

.DESCRIPTION
    Produces a single-file DeskCanvas.exe with all widgets embedded (widgets are
    extracted to %LocalAppData%\DeskCanvas on first run, so they stay hot-updatable).

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

    [switch]$Msi
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\DeskCanvas\DeskCanvas.csproj"
$dist = Join-Path $root "dist\$Runtime"

if (-not (Test-Path $project)) { throw "Project not found: $project" }

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
    --self-contained false `
    -o $dist `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)" }

# --- Strict single exe: remove stray content copied next to the exe ---
Get-ChildItem $dist -File | Where-Object {
    $_.Extension -in ".pdb", ".xml" -or $_.Name -in "appSettings.json", "layout.json", "icon.ico"
} | Remove-Item -Force -ErrorAction SilentlyContinue

$exe = Join-Path $dist "DeskCanvas.exe"
if (-not (Test-Path $exe)) { throw "Single-file exe not found: $exe" }
Write-Host "==> Single file: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)" -ForegroundColor Green

# --- Optional: classic portable layout (exe + Widgets + settings), zipped ---
if ($Portable) {
    $portableDir = Join-Path $root "dist\portable-$Runtime"
    if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
    New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

    Copy-Item $exe $portableDir
    Copy-Item (Join-Path $root "src\DeskCanvas\appSettings.json") $portableDir
    Copy-Item (Join-Path $root "src\DeskCanvas\layout.json") $portableDir

    $widgetsSrc = Join-Path $project "..\DeskCanvas\bin\$Configuration\net8.0\Widgets"
    if (-not (Test-Path $widgetsSrc)) { $widgetsSrc = Join-Path $root "src\DeskCanvas\bin\$Configuration\net8.0\Widgets" }
    if (Test-Path $widgetsSrc) {
        Copy-Item $widgetsSrc (Join-Path $portableDir "Widgets") -Recurse
    } else {
        Write-Warning "Widgets build output not found at $widgetsSrc — portable zip will lack widgets."
    }

    $zip = Join-Path $root "dist\DeskCanvas-$Runtime-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$portableDir\*" -DestinationPath $zip
    Write-Host "==> Portable zip: $zip" -ForegroundColor Green
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
