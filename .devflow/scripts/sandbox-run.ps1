# Runs inside Windows Sandbox at logon (see `dev sandbox`).
# C:\beta    = read-only beta kit (zip + dev cert)
# C:\results = writable folder shared back to the host (logs, crash dumps)
param([Parameter(Mandatory)][string]$Exe)

$ErrorActionPreference = 'Continue'
$log = 'C:\results\sandbox.log'
function Log($m) { "$(Get-Date -Format s)  $m" | Tee-Object -FilePath $log -Append }

Log "Sandbox start on $([Environment]::OSVersion.VersionString)"

# Trust the beta signing cert inside the sandbox only.
Get-ChildItem C:\beta -Filter *.cer | ForEach-Object {
    Import-Certificate -FilePath $_.FullName -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $_.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Log "Trusted $($_.Name)"
}

# Full crash dumps for the app go to the shared results folder.
$key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$Exe"
New-Item $key -Force | Out-Null
New-Item -ItemType Directory -Force C:\results\dumps | Out-Null
Set-ItemProperty $key DumpFolder 'C:\results\dumps' -Type ExpandString
Set-ItemProperty $key DumpType 2 -Type DWord

$zip = Get-ChildItem C:\beta -Filter *.zip | Where-Object Name -notmatch 'symbols' | Select-Object -First 1
$app = Join-Path $env:USERPROFILE 'Desktop\App'
Expand-Archive $zip.FullName -DestinationPath $app -Force
Log "Extracted $($zip.Name) to $app"

$exePath = Join-Path $app $Exe
$sig = Get-AuthenticodeSignature $exePath
Log "Signature: $($sig.Status) $($sig.SignerCertificate.Subject)"

$p = Start-Process $exePath -ArgumentList '--self-test' -Wait -PassThru
Log "Self-test exit code: $($p.ExitCode)"

Log 'Launching app for manual testing. Use the test plan in docs\BETA.md.'
Start-Process $exePath
