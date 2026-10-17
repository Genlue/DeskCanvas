#requires -Version 5.1
<##
.SYNOPSIS
    Release gate for DeskCanvas: aggregates the full verify pipeline with the
    release-oriented suites (API snapshot, upgrade/rollback drills, settings release).
.DESCRIPTION
    Runs, in order:
      1. scripts/verify.ps1 -NoRestore (structure/convention guards, solution build
         and every registered verify suite — inherits its whole step list)
      2. tests/ApiSnapshotChecks (Release, verify mode)
      3. tests/UpgradeRollbackChecks (Release)
      4. tests/SettingsReleaseChecks (Release)

    Every step's exit code is collected; a summary table is printed at the end.
    Any non-zero exit makes the gate exit 1. On full success the script prints
    "release gate passed" and writes a machine-readable summary to
    dist/release-gate-summary.json (timestamp, per-step result, HEAD short hash).

    The API snapshot step always runs in VERIFY mode: DESKCANVAS_API_SNAPSHOT_RECORD
    is cleared for the child process so a gate can never silently rewrite the
    committed baseline. Regenerating the baseline is an explicit integration step
    (run tests/ApiSnapshotChecks with the variable set), never a gate side effect.

    WidgetReleaseChecks and other real-hardware acceptance items are deliberately
    NOT part of this automatic gate; see the trailing note and
    docs/真机验收清单.md.

    Callable from any directory: the repository root is resolved from this script's
    own location unless -Root is given. Child programs run in separate processes so
    their exit codes cannot abort this script.
.PARAMETER Root
    Repository root. Defaults to the parent of this script directory.
.PARAMETER NoRestore
    Pass --no-restore through to verify.ps1 / dotnet (use after a prior restore).
##>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

# Child gates run under PowerShell 7 (pwsh) per the repo shell policy; fall back to the host
# executable only when pwsh is unavailable so a clean checkout still gates.
$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
$childShell = if ($pwsh) { $pwsh.Source } else { 'powershell' }
Write-Output ("child shell: {0}" -f $childShell)

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
        # 1. Full verify pipeline (guards + build + registered suites).
        $verifyArgs = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $Root 'scripts/verify.ps1'), '-Root', $Root)
        if ($NoRestore) { $verifyArgs += '-NoRestore' }
        Invoke-Step 'verify.ps1 (full pipeline)' $childShell $verifyArgs

        # The API snapshot must always verify against the committed baseline inside
        # the gate; a leftover RECORD flag in the environment must not leak in.
        Remove-Item Env:DESKCANVAS_API_SNAPSHOT_RECORD -ErrorAction SilentlyContinue

        # 2. Public API snapshot (verify mode).
        Invoke-Step 'run ApiSnapshotChecks (Release, verify)' 'dotnet' (@(
            'run', '--project', (Join-Path $Root 'tests/ApiSnapshotChecks'), '-c', 'Release') + $restoreArgs)

        # 3. Upgrade/rollback drills.
        Invoke-Step 'run UpgradeRollbackChecks (Release)' 'dotnet' (@(
            'run', '--project', (Join-Path $Root 'tests/UpgradeRollbackChecks'), '-c', 'Release') + $restoreArgs)

        # 4. Settings release contract (real settings-page teardown).
        Invoke-Step 'run SettingsReleaseChecks (Release)' 'dotnet' (@(
            'run', '--project', (Join-Path $Root 'tests/SettingsReleaseChecks'), '-c', 'Release') + $restoreArgs)
    }
    finally {
        Pop-Location
    }

    Write-Output "`n=== Summary ==="
    Write-Output ($steps | Format-Table -AutoSize | Out-String)

    $failed = @($steps | Where-Object { $_.ExitCode -ne 0 })
    if ($failed.Count -gt 0) {
        Write-Error ("release gate failed: {0} of {1} step(s) non-zero: {2}" -f $failed.Count, $steps.Count, (($failed | ForEach-Object { $_.Step }) -join ', ')) -ErrorAction Continue
        exit 1
    }

    # Machine-readable summary for the release record.
    $head = ''
    try {
        $head = (& git -C $Root rev-parse --short HEAD) 2>$null
        if ($LASTEXITCODE -ne 0) { $head = '' }
    } catch { $head = '' }

    $summary = [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        Head = $head
        Root = $Root
        Steps = @($steps | ForEach-Object { [pscustomobject]@{ Step = $_.Step; ExitCode = $_.ExitCode; Result = $_.Result } })
    }
    $distDir = Join-Path $Root 'dist'
    if (-not (Test-Path -LiteralPath $distDir -PathType Container)) {
        New-Item -ItemType Directory -Path $distDir | Out-Null
    }
    $summaryPath = Join-Path $distDir 'release-gate-summary.json'
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    Write-Output ("release gate passed: all {0} step(s) succeeded. Summary written to {1}" -f $steps.Count, $summaryPath)
    Write-Output "NOTE: this gate excludes real-hardware acceptance items (WidgetReleaseChecks and friends); see docs/真机验收清单.md before shipping."
    exit 0
}
catch {
    Write-Error ("release gate could not run: {0}" -f $_.Exception.Message) -ErrorAction Continue
    exit 2
}
