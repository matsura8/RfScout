# DevFlow: one command surface for building, testing, auditing and shipping
# native Windows apps written in C++ (CMake + MSVC), C# (.NET / WPF) or Rust.
#
# Layout inside every project:
#   devflow.json          name, type (cpp|csharp|rust), binary
#   dev.ps1               shim -> .devflow\dev.ps1
#   .devflow\             vendored copy of this runner (for CI / other machines)
#   out\                  everything generated: build, test, coverage, audit, package, sandbox, dumps

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:DevFlowRoot = Split-Path $PSScriptRoot -Parent
$script:ToolsDir = Join-Path $env:LOCALAPPDATA 'devflow\tools'

# sshd and long-lived shells keep the PATH from when they started; add the tool folders
# bootstrap installs to, so freshly installed tools work without a restart.
foreach ($d in @("$env:ProgramFiles\Cppcheck", 'C:\Tools\Sysinternals', "$env:ProgramFiles\OpenCppCoverage",
        "$env:ProgramFiles\WinGet\Links", "$env:LOCALAPPDATA\Microsoft\WinGet\Links", "$env:USERPROFILE\.local\bin",
        "$env:USERPROFILE\.cargo\bin", "$env:USERPROFILE\.dotnet\tools", "$env:LOCALAPPDATA\Microsoft\WindowsApps")) {
    if ((Test-Path $d) -and (($env:Path -split ';') -notcontains $d)) { $env:Path = "$env:Path;$d" }
}

# ---------------------------------------------------------------- helpers

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn2([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }
function Write-Info([string]$Text) { Write-Host "    $Text" -ForegroundColor DarkGray }

function Test-Tool([string]$Name) { [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

# Run a native command and throw if it fails.
function Invoke-Native {
    param([Parameter(Mandatory)][string]$File, [string[]]$Arguments = @(), [switch]$AllowFail)
    Write-Info "$File $($Arguments -join ' ')"
    & $File @Arguments
    $code = $LASTEXITCODE
    if (-not $AllowFail -and $code -ne 0) { throw "$File exited with code $code" }
    return
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Opt([hashtable]$Named, [string]$Key, $Default = $null) {
    foreach ($k in $Named.Keys) { if ($k -ieq $Key) { return $Named[$k] } }
    return $Default
}
function Test-Flag([hashtable]$Named, [string]$Key) { [bool](Get-Opt $Named $Key $false) }

function Get-VsInstallPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }
    $p = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($p) { return ($p | Select-Object -First 1) }
    return $null
}

# Load the MSVC x64 environment (cl, link, msbuild) into this process once.
function Enter-DevEnv {
    if ($env:VSCMD_VER -and (Test-Tool cl)) { return }
    $vs = Get-VsInstallPath
    if (-not $vs) { throw 'MSVC not found. Install VS 2022 Build Tools with the C++ workload (dev bootstrap).' }
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    $here = Get-Location
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
    Set-Location $here
}

function Get-SdkTool([string]$Exe) {
    $hit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\$Exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if ($hit) { return $hit.FullName }
    return $null
}

function Get-Cdb {
    foreach ($p in @("${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe")) { if (Test-Path $p) { return $p } }
    $c = Get-Command cdb -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

function Get-BinSkim {
    $hit = Get-ChildItem $script:ToolsDir -Recurse -Filter BinSkim.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match 'win-x64' | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    return $null
}

function Get-SymbolPath([string[]]$Extra) {
    $cache = Join-Path $env:LOCALAPPDATA 'devflow\symcache'
    (@($Extra) + "srv*$cache*https://msdl.microsoft.com/download/symbols") -join ';'
}

# ---------------------------------------------------------------- project model

function Find-ProjectRoot([string]$Start = (Get-Location).Path) {
    $d = Get-Item $Start
    while ($d) {
        if (Test-Path (Join-Path $d.FullName 'devflow.json')) { return $d.FullName }
        $d = $d.Parent
    }
    return $null
}

function Get-Project {
    $root = Find-ProjectRoot
    if (-not $root) { throw 'Not inside a DevFlow project (no devflow.json found). Use `dev new` to create one.' }
    $cfg = Get-Content (Join-Path $root 'devflow.json') -Raw | ConvertFrom-Json
    $p = [pscustomobject]@{
        Root    = $root
        Name    = $cfg.name
        Type    = $cfg.type
        Binary  = $cfg.binary
        Repo    = $(if ($cfg.PSObject.Properties['repo']) { $cfg.repo } else { $null })
        Out     = Join-Path $root 'out'
        Version = $null
    }
    $p.Version = Get-ProjectVersion $p
    return $p
}

function Get-ProjectVersion($P) {
    switch ($P.Type) {
        'cpp' {
            $m = Select-String -Path (Join-Path $P.Root 'CMakeLists.txt') -Pattern 'project\(\s*\S+\s+VERSION\s+([0-9.]+)' | Select-Object -First 1
            if ($m) { return $m.Matches[0].Groups[1].Value }
        }
        'csharp' {
            $m = Select-String -Path (Join-Path $P.Root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
            if ($m) { return $m.Matches[0].Groups[1].Value }
        }
        'rust' {
            $m = Select-String -Path (Join-Path $P.Root 'Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1
            if ($m) { return $m.Matches[0].Groups[1].Value }
        }
    }
    return '0.0.0'
}

function Get-BinaryPath($P, [string]$Config = 'Debug', [switch]$Asan) {
    switch ($P.Type) {
        'cpp' {
            $preset = if ($Asan) { 'asan' } else { $Config.ToLower() }
            return Join-Path $P.Out "build\$preset\$($P.Binary).exe"
        }
        'csharp' { return Join-Path $P.Out "artifacts\bin\$($P.Name)\$($Config.ToLower())\$($P.Binary).exe" }
        'rust' { return Join-Path $P.Root "target\$($Config.ToLower())\$($P.Binary).exe" }
    }
}

function New-OutDir($P, [string]$Sub) {
    $d = Join-Path $P.Out $Sub
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    return $d
}

# ---------------------------------------------------------------- build / test

function Invoke-Build($P, [string]$Config = 'Debug', [switch]$Asan, [switch]$Analyze) {
    Push-Location $P.Root
    try {
        switch ($P.Type) {
            'cpp' {
                Enter-DevEnv
                $preset = if ($Analyze) { 'analyze' } elseif ($Asan) { 'asan' } else { $Config.ToLower() }
                Write-Step "Build C++ ($preset)"
                Invoke-Native cmake @('--preset', $preset)
                Invoke-Native cmake @('--build', '--preset', $preset)
            }
            'csharp' {
                Write-Step "Build C# ($Config)"
                if ($Asan) { Write-Warn2 'ASan does not apply to managed code; doing a normal build.' }
                Invoke-Native dotnet @('build', '-c', $Config, '--nologo')
            }
            'rust' {
                Write-Step "Build Rust ($Config)"
                $a = @('build')
                if ($Config -eq 'Release') { $a += '--release' }
                if ($Asan) {
                    Write-Warn2 'Rust ASan needs nightly: rustup toolchain install nightly; then cargo +nightly build -Zbuild-std ... (see docs\DEBUGGING.md).'
                }
                Invoke-Native cargo $a
            }
        }
        $bin = Get-BinaryPath $P $Config -Asan:$Asan
        if (-not $Analyze -and (Test-Path $bin)) { Write-Ok "Built $bin" }
    } finally { Pop-Location }
}

function Invoke-Test($P, [switch]$Coverage, [switch]$Hardware) {
    if ($Hardware) {
        if ($Coverage) { Write-Warn2 'Coverage is not collected for hardware runs.' }
        Set-HardwareEnv (Assert-UsbReady $P)
    } else {
        Remove-Item Env:DEVFLOW_HARDWARE, Env:DEVFLOW_USB_DEVICES -ErrorAction SilentlyContinue
    }
    Push-Location $P.Root
    try {
        if ($Hardware) { Invoke-HardwareTests $P; return }
        switch ($P.Type) {
            'cpp' {
                Enter-DevEnv
                Invoke-Build $P 'Debug'
                Write-Step 'Test C++ (CTest)'
                if ($Coverage) {
                    if (-not (Test-Tool OpenCppCoverage)) { throw 'OpenCppCoverage not installed (dev bootstrap).' }
                    $cov = New-OutDir $P 'coverage'
                    Invoke-Native OpenCppCoverage @('--quiet', '--cover_children', '--sources', (Join-Path $P.Root 'src'),
                        '--export_type', "cobertura:$cov\cobertura.xml", '--export_type', "html:$cov\html",
                        '--', 'ctest', '--preset', 'debug', '-E', '^Hardware\.')
                    Write-Ok "Coverage report: $cov\html\index.html"
                } else {
                    Invoke-Native ctest @('--preset', 'debug', '-E', '^Hardware\.')   # hardware tests: dev test -Hardware
                }
            }
            'csharp' {
                Write-Step 'Test C# (dotnet test)'
                $res = New-OutDir $P 'test'
                # Microsoft.Testing.Platform runner (global.json); xunit.v3 writes its own TRX.
                $a = @('test', '--results-directory', $res, '--report-xunit-trx', '--filter-not-trait', 'Category=Hardware')
                if ($Coverage) { $a += @('--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', 'coverage.cobertura.xml') }
                Invoke-Native dotnet $a
                if ($Coverage) {
                    $cov = New-OutDir $P 'coverage'
                    if (Test-Tool reportgenerator) {
                        Invoke-Native reportgenerator @("-reports:$res\**\*.cobertura.xml", "-targetdir:$cov\html", '-reporttypes:Html;TextSummary')
                        Get-Content "$cov\html\Summary.txt" -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object { Write-Info $_ }
                        Write-Ok "Coverage report: $cov\html\index.html"
                    } else { Write-Warn2 "Raw coverage in $res (install reportgenerator for HTML: dev bootstrap)" }
                }
            }
            'rust' {
                Write-Step 'Test Rust'
                if ($Coverage) {
                    if (-not (Test-Tool cargo-llvm-cov)) { throw 'cargo-llvm-cov not installed (dev bootstrap).' }
                    $cov = New-OutDir $P 'coverage'
                    Invoke-Native cargo @('llvm-cov', '--html', '--output-dir', $cov)
                    Write-Ok "Coverage report: $cov\html\index.html"
                } elseif (Test-Tool cargo-nextest) {
                    Invoke-Native cargo @('nextest', 'run')
                    Invoke-Native cargo @('test', '--doc')
                } else {
                    Invoke-Native cargo @('test')
                }
            }
        }
        Invoke-SmokeTest $P 'Debug'
    } finally { Pop-Location }
}

# Runs only the tests that need real devices (convention per language):
#   C++   GoogleTest suite named Hardware          (ctest -R ^Hardware\.)
#   C#    [Trait("Category", "Hardware")]
#   Rust  #[ignore] tests in tests/hardware.rs     (cargo test --test hardware -- --ignored)
function Invoke-HardwareTests($P) {
    Write-Step "Hardware tests ($env:DEVFLOW_USB_DEVICES)"
    switch ($P.Type) {
        'cpp' {
            Enter-DevEnv
            Invoke-Build $P 'Debug'
            Invoke-Native ctest @('--preset', 'debug', '-R', '^Hardware\.', '--no-tests=error')
        }
        'csharp' {
            $res = New-OutDir $P 'test\hardware'
            Invoke-Native dotnet @('test', '--results-directory', $res, '--report-xunit-trx', '--filter-trait', 'Category=Hardware')
        }
        'rust' {
            if (-not (Test-Path (Join-Path $P.Root 'tests\hardware.rs'))) { throw 'No tests/hardware.rs (see docs\USB.md).' }
            Invoke-Native cargo @('test', '--test', 'hardware', '--', '--ignored')
        }
    }
    Write-Ok 'Hardware tests passed'
}

# Every template app supports --self-test: start up, check core logic, exit 0.
function Invoke-SmokeTest($P, [string]$Config = 'Debug') {
    $bin = Get-BinaryPath $P $Config
    if (-not (Test-Path $bin)) {
        if ($P.Type -eq 'csharp' -or $P.Type -eq 'rust') { Invoke-Build $P $Config } else { return }
    }
    Write-Step "Smoke test: $(Split-Path $bin -Leaf) --self-test"
    $proc = Start-Process -FilePath $bin -ArgumentList '--self-test' -PassThru -Wait -NoNewWindow
    if ($proc.ExitCode -ne 0) { throw "Smoke test failed (exit $($proc.ExitCode))" }
    Write-Ok 'Smoke test passed'
}

function Get-CppSources($P) {
    Get-ChildItem -Path (Join-Path $P.Root 'src'), (Join-Path $P.Root 'tests') -Recurse -Include *.cpp, *.h, *.hpp, *.cc -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
}

function Invoke-Format($P, [switch]$Check) {
    Push-Location $P.Root
    try {
        switch ($P.Type) {
            'cpp' {
                Write-Step "clang-format $(if ($Check) {'(check)'})"
                $files = @(Get-CppSources $P)
                if ($files.Count -eq 0) { return }
                $a = if ($Check) { @('--dry-run', '--Werror') } else { @('-i') }
                Invoke-Native clang-format ($a + $files)
            }
            'csharp' {
                Write-Step "dotnet format $(if ($Check) {'(check)'})"
                $a = @('format')
                if ($Check) { $a += '--verify-no-changes' }
                Invoke-Native dotnet $a
            }
            'rust' {
                Write-Step "cargo fmt $(if ($Check) {'(check)'})"
                $a = @('fmt', '--all')
                if ($Check) { $a += @('--', '--check') }
                Invoke-Native cargo $a
            }
        }
    } finally { Pop-Location }
}

function Invoke-Lint($P) {
    Invoke-Format $P -Check
    Push-Location $P.Root
    try {
        switch ($P.Type) {
            'cpp' {
                Enter-DevEnv
                Invoke-Build $P 'Debug'
                Write-Step 'clang-tidy'
                $db = Join-Path $P.Out 'build\debug'
                $srcs = @(Get-ChildItem (Join-Path $P.Root 'src') -Recurse -Include *.cpp | Select-Object -ExpandProperty FullName)
                Invoke-Native clang-tidy (@('-p', $db, '--quiet', '--warnings-as-errors=*') + $srcs)
            }
            'csharp' {
                Write-Step 'Roslyn analyzers (build with warnings as errors)'
                Invoke-Native dotnet @('build', '-c', 'Debug', '--nologo', '-warnaserror', '--no-incremental')
            }
            'rust' {
                Write-Step 'clippy'
                Invoke-Native cargo @('clippy', '--all-targets', '--all-features', '--', '-D', 'warnings')
            }
        }
        Write-Ok 'Lint clean'
    } finally { Pop-Location }
}

# ---------------------------------------------------------------- security audit

function Invoke-Audit($P, [switch]$Quick) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dir = New-OutDir $P "audit\$stamp"
    $results = [System.Collections.Generic.List[object]]::new()

    $check = {
        param([string]$Name, [string[]]$Needs, [scriptblock]$Body)
        $missing = @($Needs | Where-Object { $_ -and -not (Test-Tool $_) })
        if ($missing.Count) {
            $results.Add([pscustomobject]@{ Check = $Name; Result = 'SKIP'; Detail = "missing: $($missing -join ', ') (dev bootstrap)" })
            Write-Warn2 "SKIP $Name - missing $($missing -join ', ')"
            return
        }
        Write-Step $Name
        try {
            $detail = & $Body
            $results.Add([pscustomobject]@{ Check = $Name; Result = 'PASS'; Detail = "$detail" })
            Write-Ok "PASS $Name"
        } catch {
            $results.Add([pscustomobject]@{ Check = $Name; Result = 'FAIL'; Detail = $_.Exception.Message })
            Write-Host "    FAIL $Name - $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Push-Location $P.Root
    try {
        # 1. Secrets in the working tree and git history.
        & $check 'Secrets (gitleaks)' @('gitleaks') {
            & gitleaks git --no-banner --redact --report-format sarif --report-path "$dir\gitleaks.sarif" . 2>&1 | Out-File "$dir\gitleaks.txt"
            if ($LASTEXITCODE -ne 0) { throw "leaks found, see $dir\gitleaks.sarif" }
            & gitleaks dir --no-banner --redact . 2>&1 | Out-File -Append "$dir\gitleaks.txt"
            if ($LASTEXITCODE -ne 0) { throw "leaks found in working tree, see $dir\gitleaks.txt" }
            'no secrets'
        }

        # 2. Static analysis across languages (Semgrep registry rules).
        $rules = @('p/security-audit', 'p/secrets')
        $rules += switch ($P.Type) { 'cpp' { 'p/c' } 'csharp' { 'p/csharp' } 'rust' { 'p/rust' } }
        & $check 'SAST (semgrep)' @('semgrep') {
            $a = @('scan', '--metrics=off', '--quiet', '--error', '--sarif', '--output', "$dir\semgrep.sarif",
                '--exclude', 'out', '--exclude', 'target', '--exclude', '.devflow')
            foreach ($r in $rules) { $a += @('--config', $r) }
            $env:PYTHONUTF8 = '1'
            & semgrep @a 2>&1 | Out-File "$dir\semgrep.txt"
            if ($LASTEXITCODE -eq 1) { throw "findings, see $dir\semgrep.sarif" }
            if ($LASTEXITCODE -gt 1) { throw "semgrep error $LASTEXITCODE, see $dir\semgrep.txt" }
            "rules: $($rules -join ', ')"
        }

        # 3. Language-specific analysis and dependency vulnerabilities.
        switch ($P.Type) {
            'cpp' {
                if (-not $Quick) {
                    & $check 'MSVC code analysis (/analyze)' @('cmake') {
                        Invoke-Build $P -Analyze *>&1 | Out-File "$dir\msvc-analyze.txt"
                        'no warnings (warnings are errors)'
                    }
                }
                & $check 'cppcheck' @('cppcheck') {
                    Enter-DevEnv
                    $db = Join-Path $P.Out 'build\debug\compile_commands.json'
                    if (-not (Test-Path $db)) { Invoke-Build $P 'Debug' | Out-Null }
                    & cppcheck "--project=$db" '--enable=warning,performance,portability' '--inline-suppr' `
                        '--suppress=missingIncludeSystem' "-i$($P.Out)" '--error-exitcode=1' "--cppcheck-build-dir=$(New-OutDir $P 'cppcheck')" `
                        "--output-file=$dir\cppcheck.txt" -q 2>&1 | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "issues, see $dir\cppcheck.txt" }
                    'clean'
                }
            }
            'csharp' {
                & $check 'Vulnerable NuGet packages' @('dotnet') {
                    & dotnet restore --nologo 2>&1 | Out-Null
                    $json = & dotnet list package --vulnerable --include-transitive --format json 2>&1
                    $json | Out-File "$dir\nuget-vulnerable.json"
                    $data = ($json -join "`n") | ConvertFrom-Json
                    $vulns = @($data.projects | ForEach-Object { $_.PSObject.Properties['frameworks'] ? $_.frameworks : @() } |
                        ForEach-Object { @($_.PSObject.Properties['topLevelPackages'] ? $_.topLevelPackages : @()) + @($_.PSObject.Properties['transitivePackages'] ? $_.transitivePackages : @()) })
                    if ($vulns.Count) { throw "$($vulns.Count) vulnerable package(s), see $dir\nuget-vulnerable.json" }
                    'none'
                }
                & $check 'Deprecated NuGet packages' @('dotnet') {
                    & dotnet list package --deprecated 2>&1 | Out-File "$dir\nuget-deprecated.txt"
                    if (Select-String -Path "$dir\nuget-deprecated.txt" -Pattern '^\s+>' -Quiet) { throw "deprecated packages, see $dir\nuget-deprecated.txt" }
                    'none'
                }
            }
            'rust' {
                & $check 'RustSec advisories (cargo audit)' @('cargo-audit') {
                    & cargo audit 2>&1 | Out-File "$dir\cargo-audit.txt"
                    if ($LASTEXITCODE -ne 0) { throw "advisories, see $dir\cargo-audit.txt" }
                    'none'
                }
                & $check 'Licenses, bans, sources (cargo deny)' @('cargo-deny') {
                    & cargo deny check 2>&1 | Out-File "$dir\cargo-deny.txt"
                    if ($LASTEXITCODE -ne 0) { throw "violations, see $dir\cargo-deny.txt" }
                    'clean'
                }
            }
        }

        # 4. Dependency + misconfiguration scan of lock files (all languages).
        if (-not $Quick) {
            & $check 'Dependencies & misconfig (trivy)' @('trivy') {
                & trivy fs --quiet --scanners vuln,misconfig --severity HIGH,CRITICAL --exit-code 1 `
                    --skip-dirs out --skip-dirs target --format table --output "$dir\trivy.txt" . 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "HIGH/CRITICAL findings, see $dir\trivy.txt" }
                'no HIGH/CRITICAL'
            }
        }

        # 5. Release binary: hardening flags, SBOM, malware scan.
        $bin = Get-BinaryPath $P 'Release'
        if (-not (Test-Path $bin)) {
            Write-Step 'Release build for binary checks'
            Invoke-Build $P 'Release' *>&1 | Out-File "$dir\release-build.txt"
        }
        & $check 'Binary hardening (BinSkim)' @() {
            $bs = Get-BinSkim
            if (-not $bs) { throw 'BinSkim not installed (dev bootstrap)' }
            & $bs analyze $bin --output "$dir\binskim.sarif" --sarif-output-version Current --log ForceOverwrite --quiet 2>&1 | Out-File "$dir\binskim.txt"
            if ($LASTEXITCODE -ne 0) { throw "hardening issues, see $dir\binskim.sarif" }
            'ASLR/DEP/CFG/CET checks pass'
        }
        & $check 'SBOM (syft, CycloneDX)' @('syft') {
            & syft "dir:$($P.Root)" --exclude './out/**' --exclude './target/**' -o "cyclonedx-json=$dir\sbom.cdx.json" -q 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'syft failed' }
            "$dir\sbom.cdx.json"
        }
        & $check 'Malware scan (Defender)' @() {
            $mp = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
            if (-not (Test-Path $mp)) { throw 'MpCmdRun not found' }
            & $mp -Scan -ScanType 3 -File (Split-Path $bin) -DisableRemediation 2>&1 | Out-File "$dir\defender.txt"
            if ($LASTEXITCODE -ne 0) { throw "threat or error, see $dir\defender.txt" }
            'no threats'
        }
    } finally { Pop-Location }

    # Summary
    $md = @("# Security audit: $($P.Name) $($P.Version)", '', "Run: $stamp  ", "Commit: $(git -C $P.Root rev-parse --short HEAD 2>$null)", '',
        '| Check | Result | Detail |', '|---|---|---|')
    foreach ($r in $results) { $md += "| $($r.Check) | $($r.Result) | $($r.Detail -replace '\|', '/') |" }
    $md += @('', 'Manual items still required each release: see docs\SECURITY.md (threat model review, fuzzing, pen-test notes).')
    $md | Set-Content "$dir\summary.md" -Encoding utf8
    Write-Host ''
    $results | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "Audit report: $dir\summary.md"
    $failed = @($results | Where-Object Result -eq 'FAIL')
    if ($failed.Count) { throw "$($failed.Count) audit check(s) failed" }
    $optional = 'SBOM (syft, CycloneDX)', 'Malware scan (Defender)', 'Dependencies & misconfig (trivy)'
    $skipped = @($results | Where-Object { $_.Result -eq 'SKIP' -and $_.Check -notin $optional })
    if ($skipped.Count -and $env:CI) { throw "$($skipped.Count) audit check(s) skipped in CI; install the tools" }
}

# ---------------------------------------------------------------- USB hardware
#
# devflow.json can declare the USB devices a project talks to:
#   "usb": [ { "name": "hackrf", "vid": "1d50", "pid": "6089", "driver": "WinUSB" } ]
# `dev usb` lists/checks them, `dev test -Hardware` runs the hardware-only tests
# against them, and `dev beta` / `dev release` refuse to ship until those pass.

function Get-UsbDevices {
    # Device-level USB nodes (composite interfaces carry &MI_xx and are folded into their parent).
    Get-CimInstance Win32_PnPEntity -Filter "DeviceID LIKE 'USB\\VID%'" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.DeviceID -match '^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(&MI_([0-9A-F]{2}))?\\(.*)$') {
            [pscustomobject]@{
                Vid       = $Matches[1].ToLower()
                Pid       = $Matches[2].ToLower()
                Interface = $Matches[4]
                Serial    = $Matches[5]
                Name      = $_.Name
                Driver    = $_.Service
                Status    = $_.Status
                Class     = $_.PNPClass
            }
        }
    }
}

function Get-UsbConfig($P) {
    $cfg = Get-Content (Join-Path $P.Root 'devflow.json') -Raw | ConvertFrom-Json
    if (-not $cfg.PSObject.Properties['usb']) { return @() }
    return @($cfg.usb)
}

function Test-UsbDevice($Want, $Devices) {
    $hits = @($Devices | Where-Object { $_.Vid -eq $Want.vid.ToLower() -and $_.Pid -eq $Want.pid.ToLower() })
    $driverOk = $true
    if ($Want.PSObject.Properties['driver'] -and $Want.driver -and $hits.Count) {
        $driverOk = [bool]($hits | Where-Object { $_.Driver -ieq $Want.driver })
    }
    [pscustomobject]@{
        Name    = $Want.name
        Id      = "$($Want.vid):$($Want.pid)".ToLower()
        Present = $hits.Count -gt 0
        Driver  = (@($hits | ForEach-Object Driver | Select-Object -Unique) -join ',')
        Wanted  = $(if ($Want.PSObject.Properties['driver']) { $Want.driver } else { '' })
        Ok      = ($hits.Count -gt 0) -and $driverOk
        Status  = (@($hits | ForEach-Object Status | Select-Object -Unique) -join ',')
    }
}

# Fails unless every declared device is connected with the expected driver.
function Assert-UsbReady($P) {
    $want = @(Get-UsbConfig $P)
    if (-not $want.Count) { throw 'No USB devices declared. Add one: dev usb -Add <vid:pid> -Name <name>' }
    $devices = @(Get-UsbDevices)
    $bad = @()
    foreach ($w in $want) {
        $r = Test-UsbDevice $w $devices
        if ($r.Ok) { Write-Ok "USB $($r.Name) $($r.Id) present (driver $($r.Driver))" }
        elseif (-not $r.Present) { $bad += "$($r.Name) $($r.Id) not connected" }
        else { $bad += "$($r.Name) $($r.Id) uses driver '$($r.Driver)', expected '$($r.Wanted)' (install it with Zadig)" }
    }
    if ($bad.Count) { throw "USB check failed: $($bad -join '; ')" }
    return $want
}

# Environment the hardware tests read, so they never hard-code IDs.
function Set-HardwareEnv($Want) {
    $env:DEVFLOW_HARDWARE = '1'
    $env:DEVFLOW_USB_DEVICES = (@($Want | ForEach-Object { "$($_.name)=$($_.vid):$($_.pid)".ToLower() }) -join ';')
}

function Invoke-Usb($P, [hashtable]$Named) {
    $add = Get-Opt $Named 'Add'
    if ($add) {
        if ($add -notmatch '^([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})$') { throw 'Usage: dev usb -Add <vid:pid> -Name <name> [-Driver WinUSB]' }
        $vid = $Matches[1].ToLower(); $pid2 = $Matches[2].ToLower()
        $name = Get-Opt $Named 'Name' "usb-$vid-$pid2"
        $hit = Get-UsbDevices | Where-Object { $_.Vid -eq $vid -and $_.Pid -eq $pid2 -and -not $_.Interface } | Select-Object -First 1
        $driver = Get-Opt $Named 'Driver' $(if ($hit) { $hit.Driver } else { $null })
        $path = Join-Path $P.Root 'devflow.json'
        $cfg = Get-Content $path -Raw | ConvertFrom-Json
        $list = [System.Collections.Generic.List[object]]::new()
        if ($cfg.PSObject.Properties['usb']) { foreach ($u in $cfg.usb) { if ($u.name -ne $name) { $list.Add($u) } } }
        $entry = [ordered]@{ name = $name; vid = $vid; pid = $pid2 }
        if ($driver) { $entry.driver = $driver }
        $list.Add([pscustomobject]$entry)
        $cfg | Add-Member -NotePropertyName usb -NotePropertyValue $list.ToArray() -Force
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8
        Write-Ok "Declared USB device $name ($vid`:$pid2$(if ($driver) { ", driver $driver" })) in devflow.json"
        if (-not $hit) { Write-Warn2 'That device is not connected right now.' }
        return
    }
    $remove = Get-Opt $Named 'Remove'
    if ($remove) {
        $path = Join-Path $P.Root 'devflow.json'
        $cfg = Get-Content $path -Raw | ConvertFrom-Json
        if ($cfg.PSObject.Properties['usb']) { $cfg.usb = @($cfg.usb | Where-Object name -ne $remove) }
        $cfg | ConvertTo-Json -Depth 5 | Set-Content $path -Encoding utf8
        Write-Ok "Removed $remove"
        return
    }
    if (Test-Flag $Named 'Tree') {
        $uv = "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\usbview.exe"
        if (-not (Test-Path $uv)) { throw 'USBView not found (installed with the Windows SDK debuggers: dev bootstrap).' }
        Start-Process $uv
        return
    }

    $devices = @(Get-UsbDevices)
    $want = @(Get-UsbConfig $P)
    Write-Step 'Connected USB devices'
    $devices | Where-Object { -not $_.Interface } | Sort-Object Vid, Pid |
        Select-Object @{ n = 'VID:PID'; e = { "$($_.Vid):$($_.Pid)" } }, Name, Driver, Class, Status,
        @{ n = 'Declared'; e = { $d = $_; ($want | Where-Object { $_.vid -eq $d.Vid -and $_.pid -eq $d.Pid } | ForEach-Object name) -join ',' } } |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    if (-not $want.Count) {
        Write-Info 'No devices declared for this project. Add one: dev usb -Add <vid:pid> -Name <name>'
        return
    }
    Write-Step 'Declared devices'
    $rows = @($want | ForEach-Object { Test-UsbDevice $_ $devices })
    $rows | Select-Object Name, Id, Present, Driver, Wanted, Ok | Format-Table -AutoSize | Out-String | Write-Host
    if (@($rows | Where-Object { -not $_.Ok }).Count) {
        Write-Warn2 'Not ready for hardware tests. Plug the device in, or fix its driver with Zadig (dev bootstrap installs it).'
        if (Test-Flag $Named 'Check') { throw 'USB check failed' }
    } else { Write-Ok 'All declared devices ready: dev test -Hardware' }
}

# ---------------------------------------------------------------- debugging

function Invoke-Debug($P, [switch]$Asan) {
    Invoke-Build $P 'Debug' -Asan:$Asan
    $bin = Get-BinaryPath $P 'Debug' -Asan:$Asan
    if ($P.Type -eq 'csharp') {
        Write-Host 'Managed debugging: open the folder in VS Code and press F5 (".NET: Launch app"), or attach with dnSpy/VS.'
        if (Test-Tool code) { & code $P.Root }
        return
    }
    $windbg = Get-Command WinDbgX -ErrorAction SilentlyContinue
    if (-not $windbg) { throw 'WinDbg not installed (dev bootstrap). Or use VS Code: F5 with the "Debug (MSVC)" config.' }
    $sym = Get-SymbolPath @((Split-Path $bin))
    Write-Step "WinDbg: $bin"
    Start-Process $windbg.Source -ArgumentList @('-y', "`"$sym`"", '-srcpath', "`"$($P.Root)`"", "`"$bin`"")
}

function Invoke-Dumps($P, [hashtable]$Named) {
    $exe = "$($P.Binary).exe"
    $dumpDir = New-OutDir $P 'dumps'
    $key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$exe"
    if (Test-Flag $Named 'Enable') {
        if (-not (Test-IsAdmin)) { throw "Needs an admin shell (writes HKLM). Run as admin: dev dumps -Enable" }
        New-Item -Path $key -Force | Out-Null
        Set-ItemProperty $key DumpFolder $dumpDir -Type ExpandString
        Set-ItemProperty $key DumpType 2 -Type DWord     # full dump
        Set-ItemProperty $key DumpCount 10 -Type DWord
        Write-Ok "Crashes of $exe now write full dumps to $dumpDir"
        return
    }
    if (Test-Flag $Named 'Disable') {
        if (-not (Test-IsAdmin)) { throw 'Needs an admin shell.' }
        Remove-Item $key -Recurse -ErrorAction SilentlyContinue
        Write-Ok "Crash dumps for $exe disabled"
        return
    }
    $file = Get-Opt $Named 'Analyze'
    if ($file -eq $true -or (-not $file)) {
        $file = Get-ChildItem $dumpDir, (Join-Path $P.Out 'sandbox\results\dumps') -Filter *.dmp -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
        if (-not $file) {
            Write-Host "No dumps in $dumpDir. Enable with an admin shell: dev dumps -Enable"
            return
        }
    }
    $cdb = Get-Cdb
    if (-not $cdb) { throw 'cdb.exe not found (Windows SDK Debugging Tools; dev bootstrap installs them).' }
    $syms = @(Join-Path $P.Out 'build\release'; Join-Path $P.Out 'build\debug'; Join-Path $P.Out 'symbols';
        Join-Path $P.Root 'target\release'; Join-Path $P.Root 'target\debug') | Where-Object { Test-Path $_ }
    $report = "$file.analysis.txt"
    Write-Step "Analyzing $file"
    & $cdb -z $file -y (Get-SymbolPath $syms) -srcpath $P.Root -c '.symfix+; .reload; !analyze -v; .ecxr; kP; q' 2>&1 | Out-File $report
    Select-String -Path $report -Pattern 'FAILURE_BUCKET_ID|EXCEPTION_CODE_STR|FAULTING_SOURCE|SYMBOL_NAME|STACK_TEXT' -Context 0, 3 | ForEach-Object { $_.ToString() } | Select-Object -First 40 | Write-Host
    Write-Ok "Full analysis: $report"
}

# ---------------------------------------------------------------- packaging, signing, beta

# Signing identity. Order: DEVFLOW_SIGN_THUMBPRINT (trusted cert in CurrentUser\My),
# then the project's dev cert in the store, then a file-based dev PFX (used where the
# store can't create keys, e.g. key-based SSH logons, which have no Windows credentials).
function Get-DevCertDir($P) { Join-Path $env:LOCALAPPDATA "devflow\certs\$($P.Name)" }

function Get-DevCert($P) {
    $thumb = $env:DEVFLOW_SIGN_THUMBPRINT
    if ($thumb) {
        $c = Get-Item "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
        if ($c) { return [pscustomobject]@{ Thumbprint = $c.Thumbprint; Subject = $c.Subject; Pfx = $null; Password = $null; Cert = $c } }
    }
    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object Subject -eq "CN=$($P.Name) Dev Signing" | Sort-Object NotAfter | Select-Object -Last 1
    if ($c) { return [pscustomobject]@{ Thumbprint = $c.Thumbprint; Subject = $c.Subject; Pfx = $null; Password = $null; Cert = $c } }
    $dir = Get-DevCertDir $P
    $pfx = Join-Path $dir 'dev-signing.pfx'
    if (Test-Path $pfx) {
        $pw = Get-Content (Join-Path $dir 'dev-signing.pwd') -Raw
        $x = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Get-Content $pfx -AsByteStream -Raw), $pw.Trim(),
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)   # no key persistence (works over SSH)
        return [pscustomobject]@{ Thumbprint = $x.Thumbprint; Subject = $x.Subject; Pfx = $pfx; Password = $pw.Trim(); Cert = $x }
    }
    return $null
}

function New-FileDevCert($P) {
    $dir = Get-DevCertDir $P
    New-Item -ItemType Directory -Force $dir | Out-Null
    # Lock the folder to this user before writing; files inherit it. SID, not name: avoids local/domain mix-ups.
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    icacls $dir /inheritance:r /grant:r "*$($sid):(OI)(CI)F" /Q | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $req = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$($P.Name) Dev Signing", $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $eku = [Security.Cryptography.OidCollection]::new(); [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
    $req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $x = $req.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddYears(2))
    $pw = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    [IO.File]::WriteAllBytes((Join-Path $dir 'dev-signing.pfx'), $x.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pw))
    Set-Content (Join-Path $dir 'dev-signing.pwd') $pw -NoNewline
}

function Invoke-Cert($P, [hashtable]$Named) {
    $c = Get-DevCert $P
    if ((Test-Flag $Named 'New') -or -not $c) {
        Write-Step 'Creating self-signed code-signing certificate for beta builds'
        try {
            New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=$($P.Name) Dev Signing" `
                -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
                -NotAfter (Get-Date).AddYears(2) -KeyExportPolicy NonExportable | Out-Null
        } catch {
            Write-Warn2 "Cert store unavailable here ($($_.Exception.Message.Split([Environment]::NewLine)[0])); using a file-based dev PFX in $(Get-DevCertDir $P)."
            New-FileDevCert $P
        }
        $c = Get-DevCert $P
    }
    $dir = New-OutDir $P 'certs'
    [IO.File]::WriteAllBytes("$dir\$($P.Name)-dev.cer", $c.Cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Ok "Thumbprint $($c.Thumbprint), public cert: $dir\$($P.Name)-dev.cer"
    Write-Info 'This cert is for beta/internal builds only. Public releases need a trusted cert (see docs\RELEASE.md).'
}

function Invoke-Sign($P, [string[]]$Files) {
    $c = Get-DevCert $P
    if (-not $c) { throw 'No signing cert. Run: dev cert   (or set DEVFLOW_SIGN_THUMBPRINT)' }
    if ($c.Pfx) {
        # File-based dev cert: signtool can't open PFX keys without Windows credentials (SSH),
        # so sign with the in-memory key. An untrusted self-signed root reports UnknownError; that is expected.
        foreach ($f in $Files) {
            Write-Step "Sign $(Split-Path $f -Leaf) ($($c.Subject), file-based dev cert)"
            $r = Set-AuthenticodeSignature -FilePath $f -Certificate $c.Cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
            $check = Get-AuthenticodeSignature $f
            if ($check.SignerCertificate.Thumbprint -ne $c.Thumbprint) { throw "Signing failed on ${f}: $($r.StatusMessage)" }
        }
        return
    }
    $signtool = Get-SdkTool 'signtool.exe'
    if (-not $signtool) { throw 'signtool.exe not found (Windows SDK).' }
    $id = @('/sha1', $c.Thumbprint, '/s', 'My')
    foreach ($f in $Files) {
        Write-Step "Sign $(Split-Path $f -Leaf) ($($c.Subject))"
        & $signtool sign /fd SHA256 @id /tr http://timestamp.digicert.com /td SHA256 /q $f
        if ($LASTEXITCODE -ne 0) {
            Write-Warn2 'Timestamping failed; signing without a timestamp.'
            & $signtool sign /fd SHA256 @id /q $f
            if ($LASTEXITCODE -ne 0) { throw "signtool failed on $f" }
        }
    }
}

function Invoke-Package($P, [string]$Version, [switch]$Sign) {
    if (-not $Version) { $Version = $P.Version }
    $stage = Join-Path $P.Out 'package\stage'
    $syms = New-OutDir $P 'symbols'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $stage | Out-Null
    Push-Location $P.Root
    try {
        switch ($P.Type) {
            'cpp' {
                Invoke-Build $P 'Release'
                $bin = Get-BinaryPath $P 'Release'
                Copy-Item $bin $stage
                Copy-Item ([IO.Path]::ChangeExtension($bin, '.pdb')) $syms -ErrorAction SilentlyContinue
            }
            'csharp' {
                Write-Step 'dotnet publish (self-contained, single file, win-x64)'
                Invoke-Native dotnet @('publish', "src\$($P.Name)", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:Version=$Version", '-o', $stage, '--nologo')
                Get-ChildItem $stage -Filter *.pdb | Move-Item -Destination $syms -Force
            }
            'rust' {
                Invoke-Build $P 'Release'
                $bin = Get-BinaryPath $P 'Release'
                Copy-Item $bin $stage
                Copy-Item ([IO.Path]::ChangeExtension($bin, '.pdb')) $syms -ErrorAction SilentlyContinue
            }
        }
        foreach ($doc in 'README.md', 'LICENSE', 'CHANGELOG.md') { if (Test-Path $doc) { Copy-Item $doc $stage } }
        $exe = Join-Path $stage "$($P.Binary).exe"
        if ($Sign) { Invoke-Sign $P @($exe) }

        $pkgDir = New-OutDir $P 'package'
        $base = "$($P.Name)-$Version-win-x64"
        $zip = Join-Path $pkgDir "$base.zip"
        Remove-Item $zip -ErrorAction SilentlyContinue
        Compress-Archive -Path "$stage\*" -DestinationPath $zip
        Compress-Archive -Path "$syms\*" -DestinationPath (Join-Path $pkgDir "$base-symbols.zip") -Force -ErrorAction SilentlyContinue
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
        "$hash  $base.zip" | Set-Content (Join-Path $pkgDir "$base.zip.sha256")
        if (Test-Tool syft) { & syft "dir:$stage" -o "cyclonedx-json=$pkgDir\$base.sbom.cdx.json" -q 2>&1 | Out-Null }
        Write-Ok "Package: $zip"
        Write-Ok "SHA256:  $hash"
        return $zip
    } finally { Pop-Location }
}

function Get-NextBetaVersion($P) {
    $tags = @(git -C $P.Root tag --list "v$($P.Version)-beta.*" 2>$null)
    $n = 1 + (@($tags | ForEach-Object { if ($_ -match 'beta\.(\d+)$') { [int]$Matches[1] } } | Measure-Object -Maximum).Maximum ?? 0)
    return "$($P.Version)-beta.$n"
}

function Assert-CleanTree($P) {
    $dirty = git -C $P.Root status --porcelain 2>$null
    if ($dirty) { throw 'Working tree has uncommitted changes. Commit or stash first.' }
}

function Write-ReleaseNotes($P, [string]$Version, [string]$Path) {
    $last = git -C $P.Root describe --tags --abbrev=0 2>$null
    $range = if ($LASTEXITCODE -eq 0 -and $last) { "$last..HEAD" } else { 'HEAD' }
    $log = @(git -C $P.Root log $range --pretty=format:'- %s (%h)' --no-merges 2>$null)
    @("# $($P.Name) $Version", '', "Built $(Get-Date -Format 'yyyy-MM-dd') from $(git -C $P.Root rev-parse --short HEAD)", '',
        '## Changes', '') + $log + @('', '## How to test',
        '1. Unzip and run the exe (or use the Windows Sandbox config in the beta kit).',
        '2. Work through the beta test plan (docs\BETA.md, "Test plan").',
        '3. Report problems with the "Beta feedback" issue form; attach crash dumps from C:\results\dumps if any.') |
        Set-Content $Path -Encoding utf8
}

function Invoke-Sandbox($P, [hashtable]$Named) {
    $pkgDir = Join-Path $P.Out 'package'
    $zip = Get-ChildItem $pkgDir -Filter "$($P.Name)-*-win-x64.zip" -ErrorAction SilentlyContinue |
        Where-Object Name -notmatch 'symbols' | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $zip) { throw 'No package yet. Run: dev package  (or dev beta)' }
    $sb = New-OutDir $P 'sandbox'
    $results = New-OutDir $P 'sandbox\results'
    $kit = New-OutDir $P 'sandbox\kit'
    Remove-Item "$kit\*" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $zip.FullName $kit
    Get-ChildItem (Join-Path $P.Out 'certs') -Filter *.cer -ErrorAction SilentlyContinue | Copy-Item -Destination $kit
    Copy-Item (Join-Path $script:DevFlowRoot 'scripts\sandbox-run.ps1') $kit
    $net = if (Test-Flag $Named 'Network') { 'Default' } else { 'Disable' }
    $wsb = Join-Path $sb "$($P.Name).wsb"
    @"
<Configuration>
  <Networking>$net</Networking>
  <vGPU>Enable</vGPU>
  <ClipboardRedirection>Enable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$kit</HostFolder><SandboxFolder>C:\beta</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\beta\sandbox-run.ps1 -Exe "$($P.Binary).exe"</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content $wsb -Encoding utf8
    Write-Ok "Sandbox config: $wsb"
    if (@(Get-UsbConfig $P).Count) {
        Write-Warn2 'Windows Sandbox has no USB passthrough: device features cannot be tested there. Use a real PC or a Hyper-V VM (docs\USB.md).'
    }
    Write-Info "Results (self-test log, crash dumps) come back to: $results"
    if (Test-Flag $Named 'NoLaunch') { return }
    $exe = Join-Path $env:windir 'System32\WindowsSandbox.exe'
    if (-not (Test-Path $exe)) { throw 'Windows Sandbox is not enabled (Turn Windows features on: "Windows Sandbox").' }
    if (-not [Environment]::UserInteractive -or $env:SSH_CONNECTION) {
        Write-Warn2 'No interactive desktop in this session (SSH?). Double-click the .wsb file on the Apache desktop to start it.'
        return
    }
    Start-Process $exe -ArgumentList "`"$wsb`""
}

function Invoke-Beta($P, [hashtable]$Named) {
    $publish = Test-Flag $Named 'Publish'
    if (-not (Test-Flag $Named 'AllowDirty')) { Assert-CleanTree $P }
    $ver = Get-NextBetaVersion $P
    Write-Host "Preparing beta $ver" -ForegroundColor Magenta
    Invoke-Lint $P
    Invoke-Test $P
    if (@(Get-UsbConfig $P).Count) {
        if (Test-Flag $Named 'SkipHardware') { Write-Warn2 'Skipping hardware tests (-SkipHardware). Note it in the release notes.' }
        else { Invoke-Test $P -Hardware }
    }
    Invoke-Audit $P -Quick:(Test-Flag $Named 'Quick')
    Invoke-Cert $P @{}   # creates the dev cert if needed and exports the public .cer for the kit
    $zip = @(Invoke-Package $P $ver -Sign)[-1]   # last item is the path; earlier items are build output
    $notes = Join-Path (Split-Path $zip) "$($P.Name)-$ver-RELEASE-NOTES.md"
    Write-ReleaseNotes $P $ver $notes
    Invoke-Sandbox $P @{ NoLaunch = $true }
    Write-Ok "Beta kit ready: $zip"
    Write-Ok "Release notes: $notes"
    if ($publish) {
        if (-not (Test-Tool gh)) { throw 'gh CLI not found.' }
        Invoke-Native git @('-C', $P.Root, 'tag', '-a', "v$ver", '-m', "Beta $ver")
        Invoke-Native git @('-C', $P.Root, 'push', 'origin', "v$ver")
        $assets = @($zip, "$zip.sha256") + @(Get-ChildItem (Split-Path $zip) -Filter "*$ver*.sbom.cdx.json" | Select-Object -ExpandProperty FullName) +
            @(Get-ChildItem (Join-Path $P.Out 'certs') -Filter *.cer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        Invoke-Native gh (@('release', 'create', "v$ver", '--prerelease', '--title', "$($P.Name) $ver", '--notes-file', $notes) + $assets)
    } else {
        Write-Info 'Not published. Add -Publish to tag, push the tag, and create a GitHub pre-release.'
    }
}

function Invoke-Release($P, [hashtable]$Named) {
    Assert-CleanTree $P
    $branch = git -C $P.Root rev-parse --abbrev-ref HEAD
    if ($branch -ne 'main') { throw "Releases are cut from main (on $branch)." }
    $ver = $P.Version
    if (-not (Select-String -Path (Join-Path $P.Root 'CHANGELOG.md') -Pattern "^##\s*\[?$([regex]::Escape($ver))\]?" -Quiet)) {
        throw "CHANGELOG.md has no section for $ver."
    }
    if (git -C $P.Root tag --list "v$ver") { throw "Tag v$ver already exists. Bump the version first." }
    $c = Get-DevCert $P
    if ((-not $c -or $c.Subject -like '*Dev Signing') -and -not (Test-Flag $Named 'AllowDevCert')) {
        throw 'Only the self-signed dev cert is available. Set DEVFLOW_SIGN_THUMBPRINT to a trusted code-signing cert, or pass -AllowDevCert for an internal release.'
    }
    Invoke-Lint $P
    Invoke-Test $P
    if (@(Get-UsbConfig $P).Count) {
        if (Test-Flag $Named 'SkipHardware') { Write-Warn2 'Skipping hardware tests (-SkipHardware). Note it in the release notes.' }
        else { Invoke-Test $P -Hardware }
    }
    Invoke-Audit $P
    $zip = @(Invoke-Package $P $ver -Sign)[-1]   # last item is the path; earlier items are build output
    $notes = Join-Path (Split-Path $zip) "$($P.Name)-$ver-RELEASE-NOTES.md"
    Write-ReleaseNotes $P $ver $notes
    Write-Ok "Release candidate: $zip"
    if (Test-Flag $Named 'Publish') {
        Invoke-Native git @('-C', $P.Root, 'tag', '-a', "v$ver", '-m', "Release $ver")
        Invoke-Native git @('-C', $P.Root, 'push', 'origin', "v$ver")
        $assets = @($zip, "$zip.sha256") + @(Get-ChildItem (Split-Path $zip) -Filter "*$ver*.sbom.cdx.json" | Select-Object -ExpandProperty FullName)
        Invoke-Native gh (@('release', 'create', "v$ver", '--title', "$($P.Name) $ver", '--notes-file', $notes) + $assets)
    } else {
        Write-Info 'Not published. Add -Publish to tag, push, and create the GitHub release.'
    }
}

# ---------------------------------------------------------------- scaffolding

function ConvertTo-Kebab([string]$s) { ($s -creplace '([a-z0-9])([A-Z])', '$1-$2').ToLower() -replace '[^a-z0-9-]', '-' }

function New-Project([string]$Type, [string]$Name, [hashtable]$Named) {
    if ($Type -notin 'cpp', 'csharp', 'rust') { throw 'Usage: dev new <cpp|csharp|rust> <Name> [-Dir C:\workspace]' }
    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9]*$') { throw 'Name must be PascalCase letters/digits, e.g. NotePad2' }
    $parent = Get-Opt $Named 'Dir' 'C:\workspace'
    $dest = Join-Path $parent $Name
    if (Test-Path $dest) { throw "$dest already exists" }
    $kebab = ConvertTo-Kebab $Name
    $snake = $kebab -replace '-', '_'
    $binary = switch ($Type) { 'rust' { $kebab } default { $Name } }
    # Ordinal (case-sensitive) keys: __NAME__ and __name__ are different tokens.
    $tokens = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $tokens['__NAME__'] = $Name; $tokens['__name__'] = $kebab; $tokens['__name_snake__'] = $snake
    $tokens['__YEAR__'] = [string](Get-Date).Year; $tokens['__TYPE__'] = $Type; $tokens['__BINARY__'] = $binary

    Write-Step "Creating $Type project $Name at $dest"
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($src in @((Join-Path $script:DevFlowRoot 'templates\common'), (Join-Path $script:DevFlowRoot "templates\$Type"))) {
        Get-ChildItem $src -Recurse -File -Force | ForEach-Object {
            $rel = $_.FullName.Substring($src.Length + 1)
            foreach ($k in $tokens.Keys) { $rel = $rel.Replace($k, [string]$tokens[$k]) }
            $target = Join-Path $dest $rel
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            $text = [IO.File]::ReadAllText($_.FullName)
            foreach ($k in $tokens.Keys) { $text = $text.Replace($k, [string]$tokens[$k]) }
            $text = $text -replace "`r?`n", "`r`n"   # CRLF, matching .editorconfig
            [IO.File]::WriteAllText($target, $text, [Text.UTF8Encoding]::new($false))
        }
    }
    Update-Vendored $dest

    Push-Location $dest
    try {
        switch ($Type) {
            'csharp' {
                # Use the installed SDK's templates so package versions are current.
                Invoke-Native dotnet @('new', 'xunit', '-o', "tests\$Name.Tests", '-n', "$Name.Tests", '--framework', 'net10.0', '--force')
                Remove-Item "tests\$Name.Tests\UnitTest1.cs" -ErrorAction SilentlyContinue
                # xUnit v2 is marked deprecated (Legacy) on NuGet; move to xunit.v3 so `dev audit` starts clean.
                $tp = "tests\$Name.Tests\$Name.Tests.csproj"
                if (Select-String -Path $tp -Pattern 'Include="xunit"' -Quiet) {
                    Invoke-Native dotnet @('remove', $tp, 'package', 'xunit')
                    Invoke-Native dotnet @('add', $tp, 'package', 'xunit.v3')
                }
                Invoke-Native dotnet @('add', $tp, 'package', 'Microsoft.Testing.Extensions.CodeCoverage')
                Invoke-Native dotnet @('add', "src\$Name\$Name.csproj", 'package', 'System.Management')   # USB enumeration (WMI)
                (Get-Content "tests\$Name.Tests\$Name.Tests.csproj" -Raw) -replace '<TargetFramework>net10.0</TargetFramework>', '<TargetFramework>net10.0-windows</TargetFramework>' |
                    Set-Content "tests\$Name.Tests\$Name.Tests.csproj" -Encoding utf8 -NoNewline
                Invoke-Native dotnet @('add', "tests\$Name.Tests", 'reference', "src\$Name\$Name.csproj")
                Invoke-Native dotnet @('new', 'sln', '-n', $Name, '--force')
                $sln = @(Get-ChildItem -Filter "$Name.sln*")[0].Name
                Invoke-Native dotnet @('sln', $sln, 'add', "src\$Name\$Name.csproj", "tests\$Name.Tests\$Name.Tests.csproj")
                Invoke-Native dotnet @('restore', '--nologo')   # writes packages.lock.json
            }
            'rust' {
                Invoke-Native cargo @('add', 'windows', '--features',
                    'Win32_Foundation,Win32_Graphics_Gdi,Win32_System_LibraryLoader,Win32_UI_WindowsAndMessaging,Win32_UI_HiDpi')
                Invoke-Native cargo @('add', 'nusb@0.1')   # USB enumeration / access (pure Rust, WinUSB backend)
                Invoke-Native cargo @('add', '--build', 'embed-manifest')
                Invoke-Native cargo @('generate-lockfile')
            }
        }
        Invoke-Native git @('init', '-q', '-b', 'main')
        Write-Ok "Created $dest"
        Write-Host ''
        Write-Host "Next:  cd $dest ; dev doctor ; dev build ; dev test ; dev run" -ForegroundColor White
    } finally { Pop-Location }
}

function Update-Vendored([string]$ProjectRoot) {
    $home2 = if ($env:DEVFLOW_HOME -and (Test-Path (Join-Path $env:DEVFLOW_HOME 'lib\DevFlow.psm1'))) { $env:DEVFLOW_HOME } else { $script:DevFlowRoot }
    $vend = Join-Path $ProjectRoot '.devflow'
    if ((Resolve-Path $home2).Path -eq (Resolve-Path $vend -ErrorAction SilentlyContinue)?.Path) {
        throw 'Running from the vendored copy and DEVFLOW_HOME is not set; run the central dev (C:\workspace\devflow\bin\dev.cmd update).'
    }
    New-Item -ItemType Directory -Force "$vend\lib", "$vend\scripts" | Out-Null
    Copy-Item (Join-Path $home2 'dev.ps1') $vend -Force
    Copy-Item (Join-Path $home2 'lib\*') "$vend\lib" -Force
    Copy-Item (Join-Path $home2 'scripts\sandbox-run.ps1') "$vend\scripts" -Force
    Copy-Item (Join-Path $home2 'VERSION') $vend -Force -ErrorAction SilentlyContinue
    Write-Ok "Vendored DevFlow into $vend"
}

# ---------------------------------------------------------------- doctor / bootstrap

function Invoke-Doctor {
    $rows = [System.Collections.Generic.List[object]]::new()
    $add = { param($Area, $Name, $Ok, $Note) $rows.Add([pscustomobject]@{ Area = $Area; Tool = $Name; OK = $(if ($Ok) { 'yes' } else { 'NO' }); Note = $Note }) }
    $vs = Get-VsInstallPath
    & $add 'Build' 'MSVC (VS Build Tools)' ([bool]$vs) $vs
    foreach ($t in 'cmake', 'ninja', 'clang-format', 'clang-tidy', 'dotnet', 'cargo', 'git', 'gh') { & $add 'Build' $t (Test-Tool $t) '' }
    $sdks = if (Test-Tool dotnet) { (& dotnet --list-sdks) -join '; ' } else { '' }
    & $add 'Build' '.NET 10 SDK' ($sdks -match '^10\.|; 10\.') $sdks
    & $add 'Test' 'OpenCppCoverage' (Test-Tool OpenCppCoverage) 'C++ coverage'
    & $add 'Test' 'cargo-nextest' (Test-Tool cargo-nextest) 'Rust test runner'
    & $add 'Test' 'cargo-llvm-cov' (Test-Tool cargo-llvm-cov) 'Rust coverage'
    & $add 'Test' 'reportgenerator' (Test-Tool reportgenerator) 'C# coverage HTML'
    $windbg = (Test-Tool WinDbgX) -or [bool](Get-AppxPackage Microsoft.WinDbg -ErrorAction SilentlyContinue)
    & $add 'Debug' 'WinDbg' $windbg $(if (-not $windbg) { 'Store app: appears after a desktop logon' } else { '' })
    & $add 'Debug' 'cdb (dump analysis)' ([bool](Get-Cdb)) ''
    & $add 'Debug' 'Sysinternals (procmon/procdump)' (Test-Tool procdump) ''
    & $add 'Security' 'gitleaks' (Test-Tool gitleaks) 'secrets'
    & $add 'Security' 'semgrep' (Test-Tool semgrep) 'SAST'
    & $add 'Security' 'cppcheck' (Test-Tool cppcheck) 'C++ static analysis'
    & $add 'Security' 'cargo-audit' (Test-Tool cargo-audit) 'RustSec'
    & $add 'Security' 'cargo-deny' (Test-Tool cargo-deny) 'licenses/bans'
    & $add 'Security' 'trivy' (Test-Tool trivy) 'deps/misconfig'
    & $add 'Security' 'BinSkim' ([bool](Get-BinSkim)) 'binary hardening'
    & $add 'Security' 'syft' (Test-Tool syft) 'SBOM'
    & $add 'USB' 'USBView' (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\usbview.exe") 'device tree (dev usb -Tree)'
    & $add 'USB' 'Zadig' ((Test-Tool zadig) -or [bool](Get-Command 'zadig*' -ErrorAction SilentlyContinue)) 'install WinUSB/libusbK drivers'
    & $add 'USB' 'USBPcap + Wireshark' ((Test-Path "$env:ProgramFiles\USBPcap") -and (Test-Path "$env:ProgramFiles\Wireshark\Wireshark.exe")) 'USB traffic capture'
    & $add 'Ship' 'signtool' ([bool](Get-SdkTool 'signtool.exe')) ''
    & $add 'Ship' 'Windows Sandbox' (Test-Path "$env:windir\System32\WindowsSandbox.exe") 'beta testing'
    & $add 'Design' 'VS Code' (Test-Tool code) 'editor + debugger'
    & $add 'Design' 'Accessibility Insights' (Test-Path "${env:ProgramFiles(x86)}\AccessibilityInsights*") 'a11y FastPass'
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $missing = @($rows | Where-Object OK -eq 'NO')
    if ($missing.Count) { Write-Host "$($missing.Count) missing. Install with: dev bootstrap   (admin shell for machine-wide tools; run once more as each user with -User)" -ForegroundColor Yellow }
    else { Write-Host 'All tools present.' -ForegroundColor Green }
}

function Invoke-Bootstrap([hashtable]$Named) {
    & (Join-Path $script:DevFlowRoot 'scripts\bootstrap.ps1') -UserOnly:(Test-Flag $Named 'User') -SkipOptional:(Test-Flag $Named 'Minimal')
}

# ---------------------------------------------------------------- dispatcher

function Show-Help {
    @'
DevFlow - native Windows app workflow (C++ / C# / Rust)

Setup
  dev doctor                     check the toolchain
  dev bootstrap [-User]          install missing tools (admin: machine-wide; -User: per-user tools only)
  dev new <cpp|csharp|rust> <Name> [-Dir C:\workspace]
  dev update                     refresh this project's vendored .devflow copy

Inner loop
  dev build [-Release] [-Asan]   build (Debug by default; -Asan = AddressSanitizer, C++)
  dev run [-Release]             build and launch the app
  dev test [-Coverage]           unit tests + --self-test smoke test (no hardware needed)
  dev test -Hardware             only the tests that need the declared USB devices
  dev fmt                        apply formatters
  dev lint                       format check + clang-tidy / Roslyn analyzers / clippy (warnings = errors)

Debug
  dev debug [-Asan]              launch under WinDbg (native) or open VS Code (C#)
  dev dumps -Enable|-Disable     crash dumps for this app via WER (admin)
  dev dumps [-Analyze file.dmp]  triage the newest (or given) dump with cdb !analyze

USB hardware
  dev usb [-Check]               list connected USB devices; check the ones declared in devflow.json
  dev usb -Add <vid:pid> -Name <n> [-Driver WinUSB]   declare a device (driver auto-detected if connected)
  dev usb -Remove <n>            undeclare a device
  dev usb -Tree                  open USBView (descriptors, hubs, power)

Security
  dev audit [-Quick]             secrets, SAST, deps, licenses, binary hardening, SBOM, Defender

Ship
  dev package [-Sign]            release build -> out\package\<Name>-<ver>-win-x64.zip (+ symbols, sha256, SBOM)
  dev cert [-New]                self-signed code-signing cert for beta builds
  dev sandbox [-Network]         run the latest package in a throwaway Windows Sandbox
  dev beta [-Publish] [-Quick] [-SkipHardware]   gates (incl. hardware tests if USB declared) + signed beta kit (vX.Y.Z-beta.N); -Publish = tag + GitHub pre-release
  dev release [-Publish]         full gates on main + CHANGELOG check + trusted signing; -Publish = GitHub release
  dev ci                         what CI runs: fmt check, lint, test, audit -Quick
  dev clean                      delete out\ (and target\ for Rust)
'@ | Write-Host
}

function Invoke-DevCommand([string]$Command, [hashtable]$Named, [string[]]$Positional) {
    $cfg = if (Test-Flag $Named 'Release') { 'Release' } else { 'Debug' }
    switch ($Command.ToLower()) {
        { $_ -in 'help', '-h', '--help', '/?' } { Show-Help; return }
        'doctor' { Invoke-Doctor; return }
        'bootstrap' { Invoke-Bootstrap $Named; return }
        'new' { New-Project ($Positional | Select-Object -First 1) ($Positional | Select-Object -Skip 1 -First 1) $Named; return }
    }
    $p = Get-Project
    Write-Host "[$($p.Name) $($p.Version) | $($p.Type)]" -ForegroundColor DarkCyan
    switch ($Command.ToLower()) {
        'build' { Invoke-Build $p $cfg -Asan:(Test-Flag $Named 'Asan') }
        'run' {
            Invoke-Build $p $cfg -Asan:(Test-Flag $Named 'Asan')
            $bin = Get-BinaryPath $p $cfg -Asan:(Test-Flag $Named 'Asan')
            Write-Step "Launch $bin"
            Start-Process $bin -ArgumentList $Positional
        }
        'test' { Invoke-Test $p -Coverage:(Test-Flag $Named 'Coverage') -Hardware:(Test-Flag $Named 'Hardware') }
        'usb' { Invoke-Usb $p $Named }
        'fmt' { Invoke-Format $p }
        'lint' { Invoke-Lint $p }
        'audit' { Invoke-Audit $p -Quick:(Test-Flag $Named 'Quick') }
        'debug' { Invoke-Debug $p -Asan:(Test-Flag $Named 'Asan') }
        'dumps' { Invoke-Dumps $p $Named }
        'package' { Invoke-Package $p -Sign:(Test-Flag $Named 'Sign') | Out-Null }
        'cert' { Invoke-Cert $p $Named }
        'sign' { Invoke-Sign $p @($Positional) }
        'sandbox' { Invoke-Sandbox $p $Named }
        'beta' { Invoke-Beta $p $Named }
        'release' { Invoke-Release $p $Named }
        'ci' { Invoke-Lint $p; Invoke-Test $p; Invoke-Audit $p -Quick }
        'update' { Update-Vendored $p.Root }
        'clean' {
            Remove-Item $p.Out -Recurse -Force -ErrorAction SilentlyContinue
            if ($p.Type -eq 'rust') { Push-Location $p.Root; & cargo clean; Pop-Location }
            Write-Ok 'Cleaned'
        }
        'version' { Write-Host $p.Version }
        default { Show-Help; throw "Unknown command '$Command'" }
    }
}

Export-ModuleMember -Function Invoke-DevCommand
