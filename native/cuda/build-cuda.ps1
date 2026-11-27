<#
.SYNOPSIS
  Builds RfScoutCuda.dll, the optional CUDA accelerator for RfScout's analyze path.

.DESCRIPTION
  Compiles native\cuda\rfscout_cuda.cu with nvcc and links the CUDA runtime statically, so
  the result needs only nvcuda.dll - which the NVIDIA display driver already provides. No
  CUDA toolkit has to be present on a machine that merely runs RfScout.

  The output lands in native\x64 next to hackrf.dll, which RfScout.csproj already copies
  beside RfScout.exe, and the build prints the SHA-256 to record in native\README.md.

  A plain "dotnet build" does not run this script: RfScout stays buildable without a CUDA
  toolkit, and the app falls back to its managed DSP when the DLL is missing.

  The default targets - 5.2 and 8.9 - are the two GPUs in this fleet (a GTX 970M and an
  RTX 4080). PTX for the newest target is embedded as well, so a newer GPU JITs at load
  time rather than falling back. CUDA 12.x still supports Maxwell; CUDA 13 dropped it, so
  with a CUDA 13 toolkit pass -Arch '89' and Maxwell machines stay on the managed path.

.EXAMPLE
  pwsh -NoProfile -File native\cuda\build-cuda.ps1
  Build with the newest installed toolkit and print hashes.

.EXAMPLE
  pwsh -NoProfile -File native\cuda\build-cuda.ps1 -RunSmoke -Release
  ... and prove the kernels run on the attached GPU against the managed reference.

.EXAMPLE
  pwsh -NoProfile -File native\cuda\build-cuda.ps1 -Arch '52,75,120'
  Fat binary for Maxwell through Blackwell.
#>
[CmdletBinding()]
param(
    # CUDA toolkit root, e.g. C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9.
    # Default: the CUDA_PATH environment variables, then the newest toolkit under
    # C:\Program Files\NVIDIA GPU Computing Toolkit.
    [string]$ToolkitDir = '',

    # One or more compute capabilities, comma or semicolon separated. The default covers
    # the two GPUs in this fleet: 5.2 (GTX 970M, the oldest RfScout accepts) and 8.9 (RTX 4080).
    [string]$Arch = '52,89',

    # Where the DLL goes. Default: ..\x64, the folder the csproj copies next to the exe.
    [string]$OutDir = '',

    # Run the app's --gpu-test after building: it loads the DLL, probes the device, runs a
    # full analyze on a synthetic capture and compares every measurement against the
    # managed reference.
    [switch]$RunSmoke,

    # Build the Release output first so -RunSmoke uses it.
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$NativeDir = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $NativeDir
if (-not $OutDir) {
    $OutDir = Join-Path $NativeDir 'x64'
}

$Source = Join-Path $PSScriptRoot 'rfscout_cuda.cu'
$Target = Join-Path $OutDir 'RfScoutCuda.dll'
$BuildDir = Join-Path $RepoRoot 'out\native\cuda'
New-Item -ItemType Directory -Force -Path $OutDir, $BuildDir | Out-Null

function Find-CudaToolkit([string]$explicit) {
    $candidates = @()
    if ($explicit) { $candidates += $explicit }
    foreach ($name in 'CUDA_PATH_V13_0', 'CUDA_PATH_V12_9', 'CUDA_PATH_V12_8', 'CUDA_PATH_V12_6', 'CUDA_PATH') {
        if (Test-Path "env:$name") { $candidates += (Get-Item "env:$name").Value }
    }

    $candidates += (Get-ChildItem 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name.Substring(1)) } -Descending -ErrorAction SilentlyContinue).FullName

    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path (Join-Path $dir 'bin\nvcc.exe'))) { return $dir }
    }

    return $null
}

function Find-VcVars64 {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null |
            Select-Object -First 1
        if ($install) {
            $bat = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $bat) { return $bat }
        }
    }

    return (Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName -First 1)
}

$archList = @($Arch -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' })
if ($archList.Count -eq 0) {
    throw "-Arch '$Arch' contains no compute capability (use e.g. '52' or '52,75,120')."
}

$toolkit = Find-CudaToolkit $ToolkitDir
if (-not $toolkit) {
    throw 'nvcc not found. Install the CUDA Toolkit (https://developer.nvidia.com/cuda-downloads) ' +
        'or pass -ToolkitDir. RfScout still builds without it; only the GPU path is lost.'
}

$vcvars = Find-VcVars64
if (-not $vcvars) {
    throw 'vcvars64.bat not found. nvcc needs the Visual Studio C++ toolchain (VS 2022 Build Tools ' +
        'with the "Desktop development with C++" workload).'
}

# One SASS target per requested capability, plus PTX for the newest one: a GPU newer than
# anything in the list still runs, because the driver JITs that PTX when it loads the DLL.
$gencode = @($archList | ForEach-Object { "-gencode arch=compute_$_,code=sm_$_" })
$newest = ($archList | Sort-Object { [int]$_ } | Select-Object -Last 1)
$gencode += "-gencode arch=compute_$newest,code=compute_$newest"
$gencode = $gencode -join ' '
$flags = @(
    '-shared',
    '--cudart', 'static',
    $gencode,
    '-Wno-deprecated-gpu-targets',
    '-O3',
    '--use_fast_math',
    '-Xcompiler', '"/MT"',
    '-Xcompiler', '"/EHsc"',
    '-Xcompiler', '"/W3"',
    '-Xcompiler', '"/utf-8"',
    '-Xcompiler', '"/nologo"',
    '-o', "`"$Target`"",
    "`"$Source`""
) -join ' '

# One cmd file: load the MSVC environment, then run nvcc inside it. A file keeps the
# quoting readable, which nested cmd /c strings from PowerShell are not.
$cmd = Join-Path $BuildDir 'nvcc.cmd'
@(
    '@echo off'
    'call "{0}" >nul || exit /b 1' -f $vcvars
    'nvcc {0} || exit /b 1' -f $flags
) | Set-Content -Path $cmd -Encoding ascii

"toolkit  : $toolkit"
"msvc     : $vcvars"
"arch     : $($archList -join ', ')"
"source   : $Source"
"target   : $Target"

& $env:ComSpec /c "`"$cmd`""
if ($LASTEXITCODE -ne 0) {
    throw "nvcc failed with exit code $LASTEXITCODE"
}

# The import library and object files are build residue; only the DLL ships.
Get-ChildItem $BuildDir -Filter 'RfScoutCuda.*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$info = Get-Item $Target
# Get-FileHash is missing from this machine's Windows PowerShell (v5) utility module, so
# hash through .NET directly - it works on every host version.
$sha = ( [System.Security.Cryptography.SHA256]::Create() ).ComputeHash([System.IO.File]::ReadAllBytes($Target))
$shaText = [System.BitConverter]::ToString($sha) -replace '-',''
"`n{0,-24} {1,10:N2} KB  sha256 {2}" -f $info.Name, ($info.Length / 1KB), $shaText

# What the DLL imports. With a static runtime it must be nvcuda.dll plus system libraries:
# anything else (cudart64_12.dll, for example) would have to ship and be licensed too.
$dumpbin = $null
if ($vcvars) {
    $vsroot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $vcvars)))
    $dumpbin = Get-ChildItem (Join-Path $vsroot 'VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe') -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if ($dumpbin) {
    $imports = @(& $dumpbin /dependents $Target |
        Select-String -Pattern '^\s+\S+\.dll' |
        ForEach-Object { ($_ -split '\s+')[-1] } | Sort-Object -Unique)
    "imports  : $($imports -join ', ')"
    # nvcuda.dll is deliberately absent from the import table: the embedded static runtime
    # LoadLibrary's it when the first CUDA call runs. --gpu-test proves the driver is reachable.
    'note     : nvcuda.dll is loaded at run time by the statically linked CUDA runtime'

    $cudart = @($imports | Where-Object { $_ -like 'cudart*' })
    if ($cudart.Count -gt 0) {
        throw "the DLL dynamically imports $($cudart -join ', '); expected a statically linked runtime"
    }
} else {
    'imports  : (dumpbin not found; skipped)'
}

if ($RunSmoke) {
    $config = if ($Release) { 'Release' } else { 'Debug' }
    if ($Release) {
        "building RfScout (Release)..."
        Push-Location $RepoRoot
        try {
            dotnet build -c Release
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
        } finally {
            Pop-Location
        }
    }

    $exe = Join-Path $RepoRoot "out\artifacts\bin\RfScout\$($config.ToLowerInvariant())\RfScout.exe"
    if (-not (Test-Path $exe)) {
        throw "RfScout.exe not found at $exe - build it first (or pass -Release)"
    }

    "smoke    : $exe --gpu-test"
    $report = & $exe --gpu-test 2>&1
    $report | ForEach-Object { $_ }
    if ($LASTEXITCODE -eq 2) {
        # Exit code 2 means no usable CUDA device on this machine, not a broken build: the DLL
        # loaded and the probe answered. The kernels stay unproven until they run somewhere.
        Write-Warning '--gpu-test found no usable CUDA device here - the DLL loaded, but the kernels are untested on this machine.'
    } elseif ($LASTEXITCODE -ne 0) {
        throw "--gpu-test failed with exit code $LASTEXITCODE"
    }
}

"`nDone. Record the hash above in native\README.md, then commit the DLL with git add -f (binaries are gitignored)."
