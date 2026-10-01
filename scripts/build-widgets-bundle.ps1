#requires -Version 5.1
<#
.SYNOPSIS
    Builds the widget bundle zip embedded into the single-file DeskCanvas.exe.

.DESCRIPTION
    Publishes every widget project (flat, like the official Widgets layout),
    removes pdb/xml/runtimeconfig, and zips the result. Called by the
    BuildWidgetsBundle MSBuild target in src\DeskCanvas\DeskCanvas.csproj.

.PARAMETER WidgetsSourceDir
    Directory containing the widget projects (src\Widgets).

.PARAMETER StageDir
    Temporary flat staging directory for the widget publish output.

.PARAMETER ZipPath
    Destination path of the bundle zip (embedded as DeskCanvas.Resources.widgets.zip).

.PARAMETER Configuration
    Build configuration (Release/Debug).

.PARAMETER RuntimeIdentifier
    Target runtime, e.g. win-x64.
#>
param(
    [Parameter(Mandatory = $true)][string]$WidgetsSourceDir,
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$Configuration,
    [Parameter(Mandatory = $true)][string]$RuntimeIdentifier
)

$ErrorActionPreference = "Stop"

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

# 1. Publish every widget into one flat staging dir (files merge, like the official layout).
Get-ChildItem $WidgetsSourceDir -Directory | ForEach-Object {
    $csproj = Join-Path $_.FullName "$($_.Name).csproj"
    if (-not (Test-Path $csproj)) { return }

    & dotnet publish $csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained false `
        -o $StageDir `
        --nologo -v:q `
        /p:BuildingWidgetBundle=true

    if ($LASTEXITCODE -ne 0) { throw "Widget publish failed: $($_.Name)" }
}

# 2. Clean unneeded files (mirrors the official publish workflow).
Get-ChildItem $StageDir -Include *.pdb, *.xml -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $StageDir -Filter *.runtimeconfig.json | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. Zip the bundle.
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$StageDir\*" -DestinationPath $ZipPath -Force
Write-Host "Widget bundle: $ZipPath ($([math]::Round((Get-Item $ZipPath).Length / 1MB, 2)) MB)" -ForegroundColor Cyan
