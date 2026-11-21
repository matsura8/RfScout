# native — x64 native dependencies for RfScout

`x64\` holds the binaries RfScout P/Invokes. They are built here, not downloaded, and they are
committed so a fresh clone builds and runs without a C++ toolchain. Rebuild with:

```powershell
pwsh -NoProfile -File native\build-libhackrf.ps1                  # build + collect + print hashes
pwsh -NoProfile -File native\build-libhackrf.ps1 -RunHackrfInfo   # ... and prove the radio answers
```

The script clones the source and vcpkg under `native\src\` (gitignored) and needs git, cmake,
ninja and the VS 2022 Build Tools C++ workload. It does not need admin rights and does not
touch drivers — the HackRF is already bound to **WinUSB** (`dev usb` shows `WINUSB`).

## What is in x64

| File | Where it comes from | Needed by |
|---|---|---|
| `hackrf.dll` | `host/libhackrf` of greatscottgadgets/hackrf, built with CMake + Ninja + MSVC 14.44, x64 Release | `Services/HackRf/NativeMethods.cs` |
| `libusb-1.0.dll` | vcpkg `libusb:x64-windows@1.0.30` (upstream 1.0.30) | `hackrf.dll` |
| `pthreadVC3.dll`, `pthreadVCE3.dll`, `pthreadVSE3.dll` | vcpkg `pthreads:x64-windows@3.0.0#14` (pthreads4w 3.0.0). `hackrf.dll` links `pthreadVC3`: libhackrf runs its libusb event loop on a pthread, so an MSVC build needs a pthreads implementation. The other two flavours are shipped as well so the folder stays usable if a future build picks one. |
| `hackrf_*.exe` | `host/hackrf-tools` of the same tree | development checks (`hackrf_info`, `hackrf_transfer`); not shipped with RfScout |
| `hackrf.h` | same tree | reference for the `[LibraryImport]` declarations |

`hackrf.dll` imports only `libusb-1.0.dll` and `pthreadVC3.dll`; the `.csproj` copies every
`*.dll` from this folder next to `RfScout.exe`.

## Versions and SHA-256 (recorded 2026-11-21)

Source: `https://github.com/greatscottgadgets/hackrf.git` @ **v2026.01.3**
(`1cfe7dfe98d333450217d50e3f3a1ad0702e000f`), libhackrf **0.9.2**, tools release `git-1cfe7df`.
libusb **1.0.30** and pthreads4w **3.0.0#14**, both from vcpkg `x64-windows`. Built with
CMake 4.4 + Ninja + MSVC 19.44 (`cl` from VS 2022 BuildTools), `CMAKE_BUILD_TYPE=Release`.

```
hackrf.dll            36.50 KB             sha256 05679dca806eff82c007c9472c2d34817dc796550accf57d33a0a66748181a60
libusb-1.0.dll       162.50 KB  1.0.30.12037   sha256 39bd7aac8ea8b8f2f7139dc1112145f324353ada17f2e28fc6f6bd20d5c06428
pthreadVC3.dll        60.00 KB  3.0.0.0        sha256 2e8c07b6473c6fcfd908653a4b4c6c75ce401b7d48a27de1ce031a3be2fb344a
pthreadVCE3.dll       61.50 KB  3.0.0.0        sha256 8a40f99b232a6fc07cf1fa31e656d47164bcb78daddc774d1c83ba2209fedbe2
pthreadVSE3.dll       60.50 KB  3.0.0.0        sha256 e8f8f6cd5d83832a52917aa5b4a3ea0aac9799c05839505e1995d4e355154bef
hackrf_info.exe       15.50 KB             sha256 913b6d6c14d24e58d8b2076cb01610f533db115ba3f206b313ae071b0499c347
hackrf_transfer.exe   33.50 KB             sha256 346787f4e51e4528bc2e7ed10fd1af256fdef3318d018e2a2070fa49ef797511
hackrf_spiflash.exe   20.50 KB             sha256 ddd880263ab873d6505318b7fecf1e7a642d1684fe674bf929c343f58c497e16
hackrf_debug.exe      28.50 KB             sha256 a33088eced83bb693bbe7143feb695aba801c7c47208e09c3694d1d9043aa3b8
hackrf_clock.exe      21.00 KB             sha256 a532221c8768f31cd1070b8f73b5ea72e4e0c6bc5d0607522a7b444b4ef3a5a1
hackrf_biast.exe      18.00 KB             sha256 ea3619b02aa77d759d8d83e3a09c122857bfd5ea81d86170143cf7b825e02509
hackrf_operacake.exe  23.00 KB             sha256 fd99427e8951e0d5e711ca1c1bcca2f9f8d7ae29aec81575407c415b1449f017
hackrf_cpldjtag.exe   16.50 KB             sha256 76a34e58cb1a0459aa892a2f0ade761eef20683f54e16ee75b01a6173cdd7533
```

`hackrf.dll` carries no version resource (libhackrf embeds none); its version is the libhackrf
library version `0.9.2`, which `hackrf_info.exe` prints above and
`NativeMethods.hackrf_library_version()` returns at run time.

## hackrf_info.exe output (proof the device answers)

Run on Apache as `maxpowerit\dev`, HackRF One plugged in and bound to WinUSB, 2026-11-21:

```
hackrf_info version: git-1cfe7df
libhackrf version: git-1cfe7df (0.9.2)
Found HackRF
Index: 0
Serial number: 0000000000000000088869dc382c711b
Board ID Number: 2 (HackRF One)
Firmware Version: v2.3.1 (API:1.08)
Part ID Number: 0xa000cb3c 0x0066435c
Hardware Revision: r6
Hardware appears to have been manufactured by Great Scott Gadgets.
Hardware supported by installed firmware:
    HackRF One
There are 8 other devices on the same USB bus.
You may have problems at high sample rates.
```

Firmware API 1.08 is above the 1.02 minimum RX sweep mode needs. The "8 other devices on the
same bus" line is real — the built-in hub also feeds the card reader, smart-card reader,
Bluetooth and the MSI receiver — so if a sweep ever drops blocks at 20 Msps, check bus sharing
before blaming the code. The status bar counts dropped blocks for exactly that reason.

## Notes

- `hackrf_sweep` is **not** built: it needs FFTW3f, and RfScout does its own FFT. The upstream
  `host/hackrf-tools/src/hackrf_sweep.c` is the reference for the sweep block layout that
  `Core/Spectrum/SweepFrameAssembler` implements.
- Nothing here links a TX entry point; `NativeMethodsTests` fails the build test if a TX
  function ever appears in the wrapper.
- The 32-bit `hackrf.dll` under `D:\Downloads\sdrsharp-x86` and the 2022 clone under
  `D:\github\hackrf` are **not** used by RfScout.
