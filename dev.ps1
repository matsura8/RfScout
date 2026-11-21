# Project entry point: runs the vendored DevFlow copy so builds work without DevFlow installed.
# Usage: pwsh ./dev.ps1 <command>   (or just `dev <command>` when DevFlow's bin is on PATH)
& (Join-Path $PSScriptRoot '.devflow\dev.ps1') @args
exit $LASTEXITCODE
