<#
.SYNOPSIS
  Build hackrf.dll (libhackrf) + the HackRF command line tools for x64 Windows.

.DESCRIPTION
  Repeatable build of the RX side of libhackrf:

    1. clone greatscottgadgets/hackrf at a pinned tag (sparse: host/ only)
    2. get libusb-1.0 and pthreads from vcpkg (x64-windows) - libhackrf needs both:
       it talks to the device through libusb and runs its transfer event loop on a
       pthread, so on MSVC it needs a pthreads implementation as well
    3. configure host/ with CMake + the VS 2022 Build Tools (Ninja), x64 / Release
    4. build hackrf.dll and the tools, then copy hackrf.dll, libusb-1.0.dll,
       pthread*.dll and the tool executables into native\x64
    5. print each copied DLL's product version and SHA-256 (native\README.md records them)

  Nothing here needs admin rights, and nothing touches drivers: the HackRF must already
  be bound to WinUSB (see native\README.md).

  Usage (from the repo root, as a normal user):
    pwsh -NoProfile -File native\build-libhackrf.ps1
    pwsh -NoProfile -File native\build-libhackrf.ps1 -Clean
    pwsh -NoProfile -File native\build-libhackrf.ps1 -RunHackrfInfo

  Requires: git, cmake, ninja, VS 2022 Build Tools (C++), network access on first run.
  Source and vcpkg live under native\src (gitignored) so the repo stays small.
#>
[CmdletBinding()]
param(
    # Pinned upstream release. Bump deliberately and re-record the hashes in native\README.md.
    [string]$Tag = 'v2026.01.3',
    [string]$RepoUrl = 'https://github.com/greatscottgadgets/hackrf.git',
    [string]$VcpkgRepoUrl = 'https://github.com/microsoft/vcpkg.git',
    [string]$SourceRoot = (Join-Path $PSScriptRoot 'src'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'x64'),
    [string]$Triplet = 'x64-windows',
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$SkipClone,
    [switch]$RunHackrfInfo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hackrfDir = Join-Path $SourceRoot 'hackrf'
$vcpkgDir = Join-Path $SourceRoot 'vcpkg'
$vcpkgInstalled = Join-Path $vcpkgDir "installed\$Triplet"
$buildDir = Join-Path $hackrfDir "build-$Triplet-$Configuration"

function Write-Step([string]$message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
}

function Get-ArtifactInfo([string]$path) {
    $item = Get-Item $path
    [pscustomobject]@{
        File    = $item.Name
        SizeKB  = [math]::Round($item.Length / 1KB, 1)
        Version = $item.VersionInfo.FileVersion
        Sha256  = Get-Sha256 $path
    }
}

function Assert-Tool([string]$name) {
    if (-not (Get-Command $name -ErrorAction Ignore)) {
        throw "$name is not on PATH. Install it first (see the requirements in native\README.md)."
    }
}

# CMake finds clang first when LLVM is on PATH, and libhackrf is built with
# MSVC, so put the VS 2022 Build Tools x64 environment on PATH before configuring.
function Enter-MsvcX64 {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw "vswhere not found at $vswhere" }
    $install = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if (-not $install) { throw 'no VS 2022 installation with the C++ toolset found' }
    $install = ($install | Select-Object -First 1)
    $module = Join-Path $install 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path $module)) { throw "DevShell module missing: $module" }
    Write-Step "Entering the MSVC x64 developer shell ($install)"
    Import-Module $module
    Enter-VsDevShell -VsInstallPath $install -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
    if (-not (Get-Command cl.exe -ErrorAction Ignore)) { throw 'cl.exe is still not on PATH after Enter-VsDevShell' }
}

function Resolve-VcpkgLibrary([string]$filter, [string]$label) {
    $found = @(Get-ChildItem (Join-Path $vcpkgInstalled 'lib') -Filter $filter -File -ErrorAction Ignore |
            Sort-Object Name -Descending)   # prefer the newest ABI, e.g. pthreadVC4 over pthreadVC3
    if ($found.Count -eq 0) {
        throw "no $label ($filter) under $(Join-Path $vcpkgInstalled 'lib') - re-run without -SkipClone"
    }
    $found[0]
}

Assert-Tool git
Assert-Tool cmake
Assert-Tool ninja

if ($Clean -and (Test-Path $buildDir)) {
    Write-Step "Cleaning $buildDir"
    Remove-Item -Recurse -Force $buildDir
}

if (-not $SkipClone) {
    if (-not (Test-Path (Join-Path $hackrfDir '.git'))) {
        Write-Step "Cloning $RepoUrl $Tag (host/ only) into $hackrfDir"
        New-Item -ItemType Directory -Force -Path $SourceRoot | Out-Null
        git clone --depth 1 --branch $Tag --filter=blob:none --sparse $RepoUrl $hackrfDir
        if ($LASTEXITCODE -ne 0) { throw "git clone hackrf failed ($LASTEXITCODE)" }
        git -C $hackrfDir sparse-checkout set host
        if ($LASTEXITCODE -ne 0) { throw "git sparse-checkout failed ($LASTEXITCODE)" }
    }
    else {
        $current = (git -C $hackrfDir describe --tags 2>$null)
        Write-Step "hackrf source already present at $current (wanted $Tag)"
    }

    if (-not (Test-Path (Join-Path $vcpkgDir 'vcpkg.exe'))) {
        Write-Step "Cloning and bootstrapping vcpkg in $vcpkgDir (first run only)"
        New-Item -ItemType Directory -Force -Path $SourceRoot | Out-Null
        git clone --depth 1 $VcpkgRepoUrl $vcpkgDir
        if ($LASTEXITCODE -ne 0) { throw "git clone vcpkg failed ($LASTEXITCODE)" }
        & (Join-Path $vcpkgDir 'bootstrap-vcpkg.bat') -disableMetrics
        if ($LASTEXITCODE -ne 0) { throw "vcpkg bootstrap failed ($LASTEXITCODE)" }
    }

    Write-Step "vcpkg install libusb + pthreads ($Triplet)"
    & (Join-Path $vcpkgDir 'vcpkg.exe') install "libusb:$Triplet" "pthreads:$Triplet" --triplet $Triplet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed ($LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $vcpkgInstalled 'include\libusb-1.0\libusb.h'))) {
    throw "libusb.h is missing under $vcpkgInstalled - re-run without -SkipClone"
}
if (-not (Test-Path (Join-Path $vcpkgInstalled 'include\pthread.h'))) {
    throw "pthread.h is missing under $vcpkgInstalled - re-run without -SkipClone"
}

$libusbLib = Resolve-VcpkgLibrary 'libusb*.lib' 'libusb import library'
$pthreadLib = Resolve-VcpkgLibrary 'pthreadVC*.lib' 'pthreads import library'
Write-Step "linking against $($libusbLib.Name) and $($pthreadLib.Name)"

Enter-MsvcX64

Write-Step "Configuring host/ (CMake + Ninja + MSVC, $Configuration x64)"
# Always configure from a clean tree: FindLIBUSB/FindThreads hide their results with
# mark_as_advanced(), so a stale CMakeCache.txt (for example one first written with the
# clang compiler that is earlier on PATH) makes the re-run "forget" libusb.
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$cmakeArgs = @(
    '-S', (Join-Path $hackrfDir 'host'),
    '-B', $buildDir,
    '-G', 'Ninja',
    '-DCMAKE_C_COMPILER=cl',
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $vcpkgDir 'scripts/buildsystems/vcpkg.cmake')",
    "-DVCPKG_TARGET_TRIPLET=$Triplet",
    "-DLIBUSB_INCLUDE_DIR=$(Join-Path $vcpkgInstalled 'include\libusb-1.0')",
    "-DLIBUSB_LIBRARIES=$($libusbLib.FullName)",
    "-DPTHREADS_INCLUDE_DIR=$(Join-Path $vcpkgInstalled 'include')",
    "-DPTHREADS_LIBRARY=$($pthreadLib.FullName)",
    '-DENABLE_STATIC_LIB=ON',
    '-DENABLE_HACKRF_SWEEP=OFF'
)
& cmake @cmakeArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }

Write-Step 'Building hackrf.dll and the HackRF tools'
& cmake --build $buildDir --target hackrf hackrf_info hackrf_transfer hackrf_spiflash hackrf_debug `
    hackrf_clock hackrf_biast hackrf_operacake hackrf_cpldjtag
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }

Write-Step "Collecting artifacts into $OutDir"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$copied = New-Object System.Collections.Generic.List[object]

function Copy-Built([string]$name, [bool]$required = $true) {
    $found = @(Get-ChildItem -Path $buildDir -Recurse -Filter $name -File -ErrorAction Ignore)
    if ($found.Count -eq 0) {
        if ($required) { throw "$name was not produced by the build" }
        Write-Warning "$name was not produced; skipping"
        return
    }
    Copy-Item -Force -LiteralPath $found[0].FullName -Destination (Join-Path $OutDir $name)
    $script:copied.Add((Get-ArtifactInfo (Join-Path $OutDir $name)))
}

# hackrf.dll is the build output; libusb and pthreads come from vcpkg's bin, because which
# pthreads flavour got linked depends on whether CMake found the PThreads4W package config.
Copy-Built 'hackrf.dll'
$runtimeDlls = @(Get-ChildItem (Join-Path $vcpkgInstalled 'bin') -Filter 'libusb*.dll' -File) +
    @(Get-ChildItem (Join-Path $vcpkgInstalled 'bin') -Filter 'pthread*.dll' -File)
foreach ($dll in $runtimeDlls) {
    Copy-Item -Force -LiteralPath $dll.FullName -Destination (Join-Path $OutDir $dll.Name)
    $copied.Add((Get-ArtifactInfo (Join-Path $OutDir $dll.Name)))
}
foreach ($exe in 'hackrf_info', 'hackrf_transfer', 'hackrf_spiflash', 'hackrf_debug', 'hackrf_clock',
                 'hackrf_biast', 'hackrf_operacake', 'hackrf_cpldjtag') {
    Copy-Built "$exe.exe" $false
}

# The public header ships too: Services/HackRf/NativeMethods.cs is written against it.
Copy-Item -Force -LiteralPath (Join-Path $hackrfDir 'host\libhackrf\src\hackrf.h') -Destination (Join-Path $OutDir 'hackrf.h')

$copied | Sort-Object File | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

$commit = (git -C $hackrfDir rev-parse HEAD)
$describe = (git -C $hackrfDir describe --tags)
$packages = @(Get-ChildItem (Join-Path $vcpkgInstalled '..\vcpkg\info') -Filter '*.list' -ErrorAction Ignore |
        Where-Object { $_.BaseName -like 'libusb*' -or $_.BaseName -like 'pthreads*' } | ForEach-Object { $_.BaseName })
Write-Host "source: $RepoUrl @ $describe ($commit)"
Write-Host "vcpkg:  $($packages -join ', ')"
Write-Host @"

Record in native\README.md (built $((Get-Date).ToString('yyyy-MM-dd')) with CMake+Ninja+MSVC, $Configuration x64):
  paste the table above, plus the hackrf_info.exe output from -RunHackrfInfo.
"@

if ($RunHackrfInfo) {
    Write-Step 'Running hackrf_info.exe (proves the device answers)'
    & (Join-Path $OutDir 'hackrf_info.exe') 2>&1 | Out-Host
    Write-Host "hackrf_info exit code: $LASTEXITCODE"
}
