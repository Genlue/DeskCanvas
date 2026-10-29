#requires -Version 5.1
<#
.SYNOPSIS
    Hands the calling script over to PowerShell 7 when it was started by an older host.

.DESCRIPTION
    Explorer's "Run with PowerShell" verb (使用 PowerShell 运行) is hard-wired to
    %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe — Windows PowerShell 5.1 — via
    HKLM\SOFTWARE\Classes\SystemFileAssociations\.ps1\Shell\Windows.PowerShell.Run. Installing
    PowerShell 7 does not replace it; 7 only registers a verb under the Microsoft.PowerShellScript.1
    ProgId, which is not the effective ProgId for .ps1 on most machines.

    The repo forbids running build/verify logic on 5.1, so rather than failing a double-click we
    re-execute the script under pwsh and exit with its exit code. The script body never runs on 5.1.

    Callers must keep a `#requires -Version 5.1` floor. A 7.0 floor aborts inside the host before
    this file is ever loaded, which is exactly the failure this helper exists to avoid.

.EXAMPLE
    # at the top of a script, right after its param block
    . (Join-Path $PSScriptRoot 'ensure-pwsh7.ps1')
    Assert-Pwsh7Host -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters
#>

function Assert-Pwsh7Host {
    [CmdletBinding()]
    param(
        # Absolute path of the script that should run on 7. Pass $PSCommandPath.
        [Parameter(Mandatory = $true)][string]$ScriptPath,

        # The caller's $PSBoundParameters, forwarded verbatim to the relaunched script.
        [System.Collections.IDictionary]$BoundParameters = @{}
    )

    if ($PSVersionTable.PSVersion.Major -ge 7) { return }

    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        Write-Host ''
        Write-Host 'PowerShell 7 (pwsh) is required but was not found on PATH.' -ForegroundColor Red
        Write-Host 'Install it from https://aka.ms/powershell and run this script again.' -ForegroundColor Red
        Write-Host ''
        if ([Environment]::UserInteractive) { [void](Read-Host 'Press Enter to close') }
        exit 1
    }

    # Rebuild the argument list from the bound parameters: $args is empty once a param() block
    # has swallowed the named arguments, so forwarding $args would silently drop every switch.
    $forward = @()
    foreach ($key in $BoundParameters.Keys) {
        $value = $BoundParameters[$key]
        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) { $forward += "-$key" }
        }
        elseif ($null -ne $value -and "$value" -ne '') {
            $forward += @("-$key", [string]$value)
        }
    }

    Write-Host ('==> Windows PowerShell {0} detected; relaunching under PowerShell 7...' -f $PSVersionTable.PSVersion) -ForegroundColor Yellow

    # Native call (not Start-Process) so the child inherits this console: output stays visible and
    # $LASTEXITCODE carries the real result back to whatever launched us.
    & $pwsh.Source -NoProfile -File $ScriptPath @forward
    exit $LASTEXITCODE
}
