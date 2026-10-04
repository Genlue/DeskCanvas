#requires -Version 5.1
<##
.SYNOPSIS
    Read-only check for DeskCanvas source and widget boundaries.
.DESCRIPTION
    Verifies the repository shape and the minimum plugin contract without
    building, loading assemblies, or changing files.
    Exit codes: 0 = pass, 1 = contract failures, 2 = invocation/runtime error.
.PARAMETER Root
    Repository root. Defaults to the parent of this script directory.
.PARAMETER Quiet
    Suppress the success summary. Failures are still printed.
##>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Read-ProjectXml([string]$Path) {
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($Path)
    return ,$document
}

function Get-ProjectNodes([System.Xml.XmlDocument]$Document, [string]$Name) {
    return $Document.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='$Name']")
}

function Get-Metadata([System.Xml.XmlElement]$Node, [string]$Name) {
    if ($Node.HasAttribute($Name)) { return $Node.GetAttribute($Name).Trim() }
    $child = $Node.SelectSingleNode("*[local-name()='$Name']")
    if ($null -ne $child) { return $child.InnerText.Trim() }
    return ''
}

function Get-Property([System.Xml.XmlDocument]$Document, [string]$Name) {
    $nodes = @($Document.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']"))
    if ($nodes.Count -gt 0) { return $nodes[-1].InnerText.Trim() }
    return ''
}

function Get-ReferencePath([string]$ProjectPath, [System.Xml.XmlElement]$Reference) {
    $include = $Reference.GetAttribute('Include')
    if ([string]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) {
        throw "Unsupported ProjectReference Include in ${ProjectPath}: $include"
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ProjectPath) $include))
}

try {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
        $Root = Split-Path -Parent $Root
    }
    $rootPath = (Resolve-Path -LiteralPath $Root -ErrorAction Stop).Path
    $required = @('src', 'src/DeskCanvas', 'src/DeskCanvas.Core', 'src/Widgets', 'docs', 'scripts')
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $rootPath $relative) -PathType Container)) {
            $failures.Add("Missing required directory: $relative")
        }
    }

    $hostProject = Join-Path $rootPath 'src/DeskCanvas/DeskCanvas.csproj'
    $coreProject = Join-Path $rootPath 'src/DeskCanvas.Core/DeskCanvas.Core.csproj'
    if (-not (Test-Path -LiteralPath $hostProject -PathType Leaf)) { $failures.Add('Missing host project: src/DeskCanvas/DeskCanvas.csproj') }
    if (-not (Test-Path -LiteralPath $coreProject -PathType Leaf)) { $failures.Add('Missing SDK project: src/DeskCanvas.Core/DeskCanvas.Core.csproj') }

    $widgetRoot = Join-Path $rootPath 'src/Widgets'
    $widgets = @()
    if (Test-Path -LiteralPath $widgetRoot -PathType Container) {
        $widgets = @(Get-ChildItem -LiteralPath $widgetRoot -Directory | Sort-Object Name)
    }
    if ($widgets.Count -eq 0) { $failures.Add('No widget directories found under src/Widgets.') }

    if (Test-Path -LiteralPath $hostProject -PathType Leaf) {
        $hostXml = Read-ProjectXml $hostProject
        $hostReferences = @(Get-ProjectNodes $hostXml 'ProjectReference' | ForEach-Object { Get-ReferencePath $hostProject $_ })
        if ($hostReferences -notcontains $coreProject) { $failures.Add('Host project must reference DeskCanvas.Core.') }
        foreach ($reference in $hostReferences) {
            if ($reference.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add('Host project must not hard-code a widget project reference.')
            }
        }
    }
    if (Test-Path -LiteralPath $coreProject -PathType Leaf) {
        $coreXml = Read-ProjectXml $coreProject
        foreach ($reference in (Get-ProjectNodes $coreXml 'ProjectReference')) {
            $referencePath = Get-ReferencePath $coreProject $reference
            if ($referencePath -eq $hostProject -or $referencePath.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add('DeskCanvas.Core must not depend on host or widget projects.')
            }
        }
    }

    $packagesPath = Join-Path $rootPath 'Directory.Packages.props'
    $centralPackages = @{}
    if (-not (Test-Path -LiteralPath $packagesPath -PathType Leaf)) {
        $failures.Add('Missing Directory.Packages.props.')
    }
    else {
        $packagesXml = Read-ProjectXml $packagesPath
        if ((Get-Property $packagesXml 'ManagePackageVersionsCentrally') -ne 'true') {
            $failures.Add('Central package version management must be enabled.')
        }
        foreach ($package in (Get-ProjectNodes $packagesXml 'PackageVersion')) {
            $id = $package.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace((Get-Metadata $package 'Version'))) {
                $failures.Add('Central PackageVersion must specify Include and Version.')
            }
            elseif ($centralPackages.ContainsKey($id)) { $failures.Add("Duplicate central package version: $id") }
            else { $centralPackages[$id] = $true }
        }
    }

    # Inspect authored project files only; build outputs may contain generated projects.
    foreach ($tree in @('src', 'tests')) {
        $treePath = Join-Path $rootPath $tree
        if (-not (Test-Path -LiteralPath $treePath -PathType Container)) { continue }
        foreach ($projectFile in (Get-ChildItem -LiteralPath $treePath -Recurse -Filter '*.csproj' -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })) {
            $projectXml = Read-ProjectXml $projectFile.FullName
            if ((Get-Property $projectXml 'ManagePackageVersionsCentrally') -eq 'false') {
                $failures.Add("$($projectFile.BaseName): must use central package versions.")
            }
            foreach ($package in (Get-ProjectNodes $projectXml 'PackageReference')) {
                $id = $package.GetAttribute('Include')
                if ([string]::IsNullOrWhiteSpace($id)) { $id = $package.GetAttribute('Update') }
                if ((Get-Metadata $package 'Version') -ne '') {
                    $failures.Add("$($projectFile.BaseName): $id version must be declared centrally.")
                }
                if (-not $centralPackages.ContainsKey($id)) { $failures.Add("$($projectFile.BaseName): no central version for $id.") }
            }
        }
    }

    foreach ($widget in $widgets) {
        $name = $widget.Name
        $project = Join-Path $widget.FullName "$name.csproj"
        $assemblyInfo = Join-Path $widget.FullName 'AssemblyInfo.cs'
        $locale = Join-Path $widget.FullName 'Locales/Locale.resx'
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { $failures.Add("${name}: missing $name.csproj") ; continue }
        if (-not (Test-Path -LiteralPath $assemblyInfo -PathType Leaf)) { $failures.Add("${name}: missing AssemblyInfo.cs") }
        if (-not (Test-Path -LiteralPath $locale -PathType Leaf)) { $failures.Add("${name}: missing Locales/Locale.resx") }
        $projectXml = Read-ProjectXml $project
        $references = @(Get-ProjectNodes $projectXml 'ProjectReference')
        $referencePaths = @($references | ForEach-Object { Get-ReferencePath $project $_ })
        if ($referencePaths -notcontains $coreProject) { $failures.Add("${name}: project does not reference DeskCanvas.Core.") }
        foreach ($reference in $references) {
            $referencePath = Get-ReferencePath $project $reference
            if ($referencePath -ne $hostProject -and $referencePath -ne $coreProject) {
                $failures.Add("${name}: project reference must target host or Core: $($reference.GetAttribute('Include'))")
            }
            if ((Get-Metadata $reference 'Private') -ne 'false' -or @((Get-Metadata $reference 'ExcludeAssets') -split ';' | ForEach-Object { $_.Trim() }) -notcontains 'runtime') {
                $failures.Add("${name}: each project reference must set Private=false and ExcludeAssets=runtime.")
            }
        }
        $outputPath = (Get-Property $projectXml 'OutputPath').Replace('/', '\').TrimEnd('\')
        $expectedOutput = '..\..\DeskCanvas\bin\$(Configuration)\$(TargetFramework)\Widgets'
        if ($name -eq 'Music') { $expectedOutput = '..\..\DeskCanvas\bin\$(Configuration)\net8.0\Widgets' }
        if ($outputPath -ne $expectedOutput -or (Get-Property $projectXml 'AppendTargetFrameworkToOutputPath') -ne 'false') {
            $failures.Add("${name}: output must target the host Widgets directory without appending a framework.")
        }
        foreach ($package in (Get-ProjectNodes $projectXml 'PackageReference')) {
            $id = $package.GetAttribute('Include')
            if ($id -like 'Avalonia*' -or $id -eq 'SkiaSharp') {
                if ((Get-Metadata $package 'PrivateAssets') -ne 'all' -or @((Get-Metadata $package 'ExcludeAssets') -split ';' | ForEach-Object { $_.Trim() }) -notcontains 'runtime') {
                    $failures.Add("${name}: shared runtime package $id must set PrivateAssets=all and ExcludeAssets=runtime.")
                }
            }
        }
        if (Test-Path -LiteralPath $assemblyInfo) {
            $assemblyText = Get-Content -LiteralPath $assemblyInfo -Raw
            if ($assemblyText -notmatch 'WidgetInfo\s*\(') { $failures.Add("${name}: AssemblyInfo.cs has no WidgetInfo attribute.") }
            if ($assemblyText -notmatch 'Locale\s*\(') { $failures.Add("${name}: AssemblyInfo.cs has no Locale attribute.") }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Error ("Structure check failed with {0} issue(s):`n - {1}" -f $failures.Count, ($failures -join "`n - ")) -ErrorAction Continue
        exit 1
    }

    if (-not $Quiet) {
        Write-Output ("Structure check passed: {0} widget projects; host/Core boundaries and plugin contracts are present." -f $widgets.Count)
    }
    exit 0
}
catch {
    Write-Error ("Structure check could not run: {0}" -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}
