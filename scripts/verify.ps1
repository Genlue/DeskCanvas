#requires -Version 5.1
<##
.SYNOPSIS
    Unified verification entry for DeskCanvas (P0 clean-checkout gate).
.DESCRIPTION
    Runs, in order:
      1. scripts/check-structure.ps1
      2. scripts/check-ui-conventions.ps1
      3. dotnet build src/DeskCanvas.sln -c Debug
      4. dotnet run --project <suite> -c Release for the verified check suites,
         including P1-P5 architecture gates and the six existing UI/layout suites.

    Every step's exit code is collected; a summary table is printed at the end and
    the script exits 1 if any step was non-zero (0 = all pass, 2 = runtime error).

    Callable from any directory: the repository root is resolved from this script's
    own location unless -Root is given. Child check scripts are invoked in a
    separate powershell process so their `exit` statements cannot abort this script.

    The full gate list beyond the six verified suites is registered in
    tests/README.md; add them here as they are validated.
.PARAMETER Root
    Repository root. Defaults to the parent of this script directory.
.PARAMETER NoRestore
    Pass --no-restore through to dotnet build / dotnet run (use after a prior restore).
##>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($Root)) {
        $Root = Split-Path -Parent $PSScriptRoot
    }
    $Root = (Resolve-Path $Root).Path

    $restoreArgs = @()
    if ($NoRestore) { $restoreArgs = @('--no-restore') }

    $steps = New-Object System.Collections.Generic.List[object]

    function Invoke-Step {
        param(
            [string]$Name,
            [string]$FilePath,
            [string[]]$Arguments
        )
        Write-Output ("`n=== {0} ===" -f $Name)
        Write-Output ('$ ' + $FilePath + ' ' + ($Arguments -join ' '))
        & $FilePath @Arguments
        $code = $LASTEXITCODE
        $steps.Add([pscustomobject]@{ Step = $Name; ExitCode = $code; Result = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }) })
        Write-Output ("--- {0}: exit {1}" -f $Name, $code)
    }

    Push-Location $Root
    try {
        Invoke-Step 'check-structure' 'powershell' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $Root 'scripts/check-structure.ps1'), '-Root', $Root)

        Invoke-Step 'check-ui-conventions' 'powershell' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $Root 'scripts/check-ui-conventions.ps1'), '-Root', $Root)

        Invoke-Step 'build DeskCanvas.sln (Debug)' 'dotnet' (@(
            'build', (Join-Path $Root 'src/DeskCanvas.sln'), '-c', 'Debug') + $restoreArgs)

        $verifiedSuites = @(
            'IdentityStorageChecks',
            'ContractsLifecycleChecks',
            'SchedulerChecks',
            'UiSharedChecks',
            'UiSharedAvaloniaChecks',
            'PanelShellAvaloniaChecks',
            'PerScreenThemeChecks',
            'ThemeSettingsChecks',
            'SettingsIntegrationChecks',
            'WeatherSharedChecks',
            'ProviderIntegrationChecks',
            'SharedDataSourcesChecks',
            'PanelOwnershipChecks',
            'WindowRoleChecks',
            'ResourceLedgerChecks',
            'ResourcePressureChecks',
            'SessionHookChecks',
            'CalendarSessionChecks',
            'ProgressChecks',
            'BatteriesChecks',
            'ApiSnapshotChecks',
            'UiFoundationChecks',
            'LayoutOwnershipChecks',
            'FocusSwitchChecks',
            'MonochromeAccentChecks',
            'WidgetAdaptiveChecks',
            'FullscreenChecks'
        )
        foreach ($suite in $verifiedSuites) {
            Invoke-Step ("run " + $suite + " (Release)") 'dotnet' (@(
                'run', '--project', (Join-Path $Root ('tests/' + $suite)), '-c', 'Release') + $restoreArgs)
        }
    }
    finally {
        Pop-Location
    }

    Write-Output "`n=== Summary ==="
    Write-Output ($steps | Format-Table -AutoSize | Out-String)

    $failed = @($steps | Where-Object { $_.ExitCode -ne 0 })
    if ($failed.Count -gt 0) {
        Write-Error ("verify failed: {0} of {1} step(s) non-zero: {2}" -f $failed.Count, $steps.Count, (($failed | ForEach-Object { $_.Step }) -join ', ')) -ErrorAction Continue
        exit 1
    }

    Write-Output ("verify passed: all {0} step(s) succeeded." -f $steps.Count)
    exit 0
}
catch {
    Write-Error ("verify could not run: {0}" -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}
