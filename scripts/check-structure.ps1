#requires -Version 5.1
<##
.SYNOPSIS
    Read-only check for DeskCanvas source and widget boundaries.
.DESCRIPTION
    Verifies the repository shape and the minimum plugin contract without
    building, loading assemblies, or changing files. Includes the WinUI entry
    project (src/DeskCanvas.WinUI) and the one migration-scoped exception:
    only the WinUI entry may reference src/Widgets projects (report §3 step 1.5).
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
    $required = @('src', 'src/DeskCanvas', 'src/DeskCanvas.Core', 'src/DeskCanvas.Contracts', 'src/DeskCanvas.UI', 'src/DeskCanvas.Runtime', 'src/DeskCanvas.WinUI', 'src/Widgets', 'docs', 'scripts')
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $rootPath $relative) -PathType Container)) {
            $failures.Add("Missing required directory: $relative")
        }
    }

    $hostProject = Join-Path $rootPath 'src/DeskCanvas/DeskCanvas.csproj'
    $coreProject = Join-Path $rootPath 'src/DeskCanvas.Core/DeskCanvas.Core.csproj'
    $runtimeProject = Join-Path $rootPath 'src/DeskCanvas.Runtime/DeskCanvas.Runtime.csproj'
    $uiProject = Join-Path $rootPath 'src/DeskCanvas.UI/DeskCanvas.UI.csproj'
    $contractsProject = Join-Path $rootPath 'src/DeskCanvas.Contracts/DeskCanvas.Contracts.csproj'
    $winUiProject = Join-Path $rootPath 'src/DeskCanvas.WinUI/DeskCanvas.WinUI.csproj'
    $solutionPath = Join-Path $rootPath 'src/DeskCanvas.sln'
    if (-not (Test-Path -LiteralPath $hostProject -PathType Leaf)) { $failures.Add('Missing host project: src/DeskCanvas/DeskCanvas.csproj') }
    if (-not (Test-Path -LiteralPath $coreProject -PathType Leaf)) { $failures.Add('Missing SDK project: src/DeskCanvas.Core/DeskCanvas.Core.csproj') }
    if (-not (Test-Path -LiteralPath $winUiProject -PathType Leaf)) { $failures.Add('Missing WinUI entry project: src/DeskCanvas.WinUI/DeskCanvas.WinUI.csproj') }

    $widgetRoot = Join-Path $rootPath 'src/Widgets'
    $widgets = @()
    if (Test-Path -LiteralPath $widgetRoot -PathType Container) {
        $widgets = @(Get-ChildItem -LiteralPath $widgetRoot -Directory | Sort-Object Name)
    }
    if ($widgets.Count -eq 0) { $failures.Add('No widget directories found under src/Widgets.') }

    if (Test-Path -LiteralPath $hostProject -PathType Leaf) {
        $hostXml = Read-ProjectXml $hostProject
        # The host stopped being the executable entry when the GUI moved to WinUI:
        # it is now a referenced library (report §3 step 1.2). A stray WinExe/Exe here
        # would reintroduce a second startup path and duplicate the apphost.
        $hostOutputType = Get-Property $hostXml 'OutputType'
        if ($hostOutputType -ne 'Library') {
            $failures.Add("Host project must be OutputType=Library (found '$hostOutputType'); the only entry is src/DeskCanvas.WinUI.")
        }
        $hostReferences = @(Get-ProjectNodes $hostXml 'ProjectReference' | ForEach-Object { Get-ReferencePath $hostProject $_ })
        if ($hostReferences -notcontains $coreProject) { $failures.Add('Host project must reference DeskCanvas.Core.') }
        foreach ($reference in $hostReferences) {
            if ($reference.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add('Host project must not hard-code a widget project reference.')
            }
            if ($reference -eq $winUiProject) {
                $failures.Add('Host project must not reference the WinUI entry (the GUI entry depends on the host, not the reverse).')
            }
        }
    }

    $winUiWidgetReferenceCount = 0
    if (Test-Path -LiteralPath $winUiProject -PathType Leaf) {
        $winUiXml = Read-ProjectXml $winUiProject
        $winUiReferences = @(Get-ProjectNodes $winUiXml 'ProjectReference' | ForEach-Object { Get-ReferencePath $winUiProject $_ })
        foreach ($requiredReference in @($hostProject, $coreProject, $contractsProject, $runtimeProject)) {
            if ($winUiReferences -notcontains $requiredReference) {
                $failures.Add("WinUI entry must reference $(Split-Path -Leaf $requiredReference).")
            }
        }

        # 报告 §3 第一步 5：27 个内置组件的新编辑页要复用模型/Store，只给 WinUI 开这个口子；
        # 其余工程仍按原边界检查（宿主不得静态依赖组件，Core/Runtime/UI/组件不得反向依赖 WinUI）。
        $allowedWinUiReferences = @($hostProject, $coreProject, $contractsProject, $runtimeProject)
        foreach ($reference in (Get-ProjectNodes $winUiXml 'ProjectReference')) {
            $referencePath = Get-ReferencePath $winUiProject $reference
            if ($referencePath.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $winUiWidgetReferenceCount++
                continue
            }
            if ($allowedWinUiReferences -notcontains $referencePath) {
                $failures.Add("WinUI entry may reference only the host/Core/Contracts/Runtime and explicit src/Widgets projects; unexpected ProjectReference: $($reference.GetAttribute('Include'))")
            }
        }

        foreach ($projectFile in @(Get-ChildItem -LiteralPath (Join-Path $rootPath 'src') -Recurse -Filter '*.csproj' -File |
            Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' })) {
            if ($projectFile.FullName -eq $winUiProject) { continue }
            $isWidgetProject = $projectFile.FullName.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
            $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile.FullName)
            $projectXml = Read-ProjectXml $projectFile.FullName
            foreach ($reference in (Get-ProjectNodes $projectXml 'ProjectReference')) {
                $referencePath = Get-ReferencePath $projectFile.FullName $reference
                if ($referencePath -eq $winUiProject) {
                    $failures.Add("${projectName}: must not reference the WinUI entry (reverse dependency; components reach the GUI through ISoftwareUi).")
                }
                elseif (-not $isWidgetProject -and
                    $referencePath.StartsWith($widgetRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $failures.Add("${projectName}: only src/DeskCanvas.WinUI may reference src/Widgets projects (migration-scope exception).")
                }
            }
        }
    }

    # 解决方案必须登记唯一新入口，避免“工程在磁盘上但没被任何入口构建”。
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        $failures.Add('Missing solution: src/DeskCanvas.sln')
    }
    elseif ((Get-Content -LiteralPath $solutionPath -Raw).IndexOf('DeskCanvas.WinUI\DeskCanvas.WinUI.csproj', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $failures.Add('Solution must list the WinUI entry project: src/DeskCanvas.WinUI/DeskCanvas.WinUI.csproj')
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

    # The new libraries have a strict boundary even while Legacy widgets retain
    # their registered host/Core compatibility references.
    foreach ($library in @(
        @{ Path = $contractsProject; Allowed = @() },
        @{ Path = $runtimeProject; Allowed = @($contractsProject) },
        @{ Path = $uiProject; Allowed = @($contractsProject) }
    )) {
        if (-not (Test-Path -LiteralPath $library.Path -PathType Leaf)) {
            $failures.Add("Missing architecture project: $($library.Path)")
            continue
        }
        $libraryXml = Read-ProjectXml $library.Path
        $libraryName = [System.IO.Path]::GetFileNameWithoutExtension($library.Path)
        foreach ($reference in (Get-ProjectNodes $libraryXml 'ProjectReference')) {
            if ($library.Allowed -notcontains (Get-ReferencePath $library.Path $reference)) {
                $failures.Add("${libraryName}: project dependency crosses the Contracts/UI/Runtime boundary.")
            }
        }
        if ($library.Path -ne $uiProject) {
            foreach ($package in (Get-ProjectNodes $libraryXml 'PackageReference')) {
                $id = $package.GetAttribute('Include')
                if ($id -match '^(Avalonia|SkiaSharp|System\.Management|Microsoft\.Windows)') {
                    $failures.Add("${libraryName}: UI and Windows package $id belongs outside the runtime/contracts boundary.")
                }
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
            if ($referencePath -eq $winUiProject) {
                $failures.Add("${name}: widget project must not reference the WinUI entry (reverse dependency).")
                continue
            }
            if ($referencePath -ne $hostProject -and $referencePath -ne $coreProject -and $referencePath -ne $runtimeProject -and $referencePath -ne $uiProject) {
                $failures.Add("${name}: project reference must target host or Core: $($reference.GetAttribute('Include'))")
            }
            if ((Get-Metadata $reference 'Private') -ne 'false' -or @((Get-Metadata $reference 'ExcludeAssets') -split ';' | ForEach-Object { $_.Trim() }) -notcontains 'runtime') {
                $failures.Add("${name}: each project reference must set Private=false and ExcludeAssets=runtime.")
            }
        }
        $outputPath = (Get-Property $projectXml 'OutputPath').Replace('/', '\').TrimEnd('\')
        $expectedOutput = '..\..\DeskCanvas\bin\$(Configuration)\$(TargetFramework)\Widgets'
        # Platform-specific widgets (Windows APIs) keep a windows TFM but still output to the
        # host's net10.0\Widgets directory.
        if ($name -eq 'Music' -or $name -eq 'ControlCenter' -or $name -eq 'Shortcuts') { $expectedOutput = '..\..\DeskCanvas\bin\$(Configuration)\net10.0\Widgets' }
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
        Write-Output ("Structure check passed: {0} widget projects; host is a Library; WinUI entry present in the solution and references {1} widget project(s); no reverse dependency on the WinUI entry; host/Core and Contracts/UI/Runtime boundaries are present; central package versions enforced." -f $widgets.Count, $winUiWidgetReferenceCount)
    }
    exit 0
}
catch {
    Write-Error ("Structure check could not run: {0}" -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}
