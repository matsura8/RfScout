<#
.SYNOPSIS
  DevFlow task runner for native Windows apps (C++, C#, Rust).

.DESCRIPTION
  Run `dev help` for the command list. Inside a project, `dev` finds the
  project's devflow.json by searching upward from the current folder.

  Projects created with `dev new` carry their own copy of this runner in
  .devflow\ so CI and other machines can build them without DevFlow
  installed. `dev update` refreshes that copy.
#>
# No param() block on purpose: flags like -Release must reach $args untouched.
$Command = if ($args.Count) { [string]$args[0] } else { 'help' }
$Rest = @(if ($args.Count -gt 1) { $args[1..($args.Count - 1)] | ForEach-Object { [string]$_ } })

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error 'DevFlow needs PowerShell 7 (pwsh). Run it with: pwsh -File dev.ps1 ...'
    exit 2
}

Import-Module (Join-Path $PSScriptRoot 'lib\DevFlow.psm1') -Force -DisableNameChecking

# Turn "-Release -Name Foo bar" into a hashtable + positional list.
$named = @{}
$positional = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $Rest.Count; $i++) {
    $a = $Rest[$i]
    if ($a -match '^-{1,2}([A-Za-z][\w-]*)$') {
        $key = $Matches[1]
        if ($i + 1 -lt $Rest.Count -and $Rest[$i + 1] -notmatch '^-{1,2}[A-Za-z]') {
            $named[$key] = $Rest[$i + 1]; $i++
        } else {
            $named[$key] = $true
        }
    } else {
        $positional.Add($a)
    }
}

try {
    Invoke-DevCommand -Command $Command -Named $named -Positional $positional.ToArray()
    exit 0
} catch {
    Write-Host ''
    Write-Host "dev $Command failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($env:DEVFLOW_DEBUG) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 1
}
