# RfScout

**A receive-only HackRF One spectrum scanner for Windows.** RfScout sweeps up to 6 GHz, shows a live
spectrum and waterfall, finds the signals worth looking at, and works out what kind of device is
behind each one.

> **Status: early development.** The sweep DSP pipeline, the display maths and the HackRF receive
> layer are written and unit-tested; the live UI is being wired up (milestone 1). Peak tracking, signal analysis and device
> identification are next. See [Roadmap](#roadmap).

## What it does

- **Wideband sweep** *(in progress)*. It uses the HackRF's hardware sweep mode (the same mode
  `hackrf_sweep` uses) to cover 1 MHz–6 GHz, or any band preset, at a resolution down to about
  4.9 kHz per bin.
- **Spectrum and waterfall** *(in progress)*. A live trace with peak hold, colour-mapped waterfall,
  automatic dB range, zoom and pan. The display maths is done; the WPF plots are being wired up.
- **Find peaks** *(planned)*. An adaptive noise floor and threshold, with each peak's centre
  frequency, bandwidth, SNR and duty cycle, tracked over time with a stable ID and a band-plan label.
- **Analyse a peak** *(planned)*. RfScout tunes to it, captures a short I/Q recording and
  classifies the modulation (AM, FM, OOK, FSK, PSK-like, OFDM, chirp…), showing the measurements
  behind each decision. Recordings are saved as `.cs8` with SigMF metadata.
- **Identify devices** *(planned)*. It decodes the public identifiers devices broadcast: ISM-band
  sensors and remotes (through [rtl_433](https://github.com/merbanan/rtl_433)), aircraft ADS-B
  (Mode S), FM station names (RDS) and ship AIS. Anything else is classified by band, modulation,
  bandwidth and burst pattern.
- **Locate** *(planned)*. A large signal-strength meter and history graph for hunting a transmitter
  down with a directional antenna. One HackRF measures strength, not direction.

### Receive only

RfScout never transmits. The native wrapper exposes no transmit functions, and a unit test fails
the build if one appears. Antenna (bias-tee) power and the RF amplifier start **off**, and turning
either on asks for confirmation. RfScout never decrypts anything.

Receiving and decoding radio signals is regulated differently from country to country. Make sure
your use is legal where you are.

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11, x64 |
| Radio | HackRF One. Sweep mode needs firmware with USB API 1.02 or later (developed with firmware v2.3.1). |
| Driver | **WinUSB**, as installed by the HackRF Windows drivers or by [Zadig](https://zadig.akeo.ie/) |
| To build | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| To rebuild the native DLLs (optional) | git, CMake, Ninja, Visual Studio 2022 Build Tools with the C++ workload |

The native dependencies (`hackrf.dll`, `libusb-1.0.dll`, pthreads) are committed in
[`native/x64`](native), so a normal build needs only the .NET SDK.

## Build and run

```powershell
git clone https://github.com/matsura8/RfScout.git
cd RfScout
dotnet build -c Release
.\out\artifacts\bin\RfScout\release\RfScout.exe
```

Output goes to `out\artifacts` (see `Directory.Build.props`). For a debug build, use
`dotnet build` and run `out\artifacts\bin\RfScout\debug\RfScout.exe`.

`RfScout.exe --self-test` runs the built-in checks (FFT, sweep framing, sweep maths) without a
radio, and exits with 0 when they pass.

## Tests and checks

Tests use xUnit v3 on Microsoft.Testing.Platform (see `global.json`). The DSP, framing and display
code is tested against synthetic signals, so no radio is needed.

```powershell
dotnet test --filter-not-trait Category=Hardware   # unit tests, no radio needed
dotnet format --verify-no-changes                  # formatting (.editorconfig)
dotnet build -c Release -warnaserror               # analyzers, including the security rules, as errors
```

CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs these steps plus the
`--self-test` smoke run on every push and pull request.

**Hardware tests** need a HackRF plugged in:

```powershell
$env:HARDWARE_TESTS = '1'
$env:HARDWARE_USB_DEVICES = 'hackrf=1d50:6089'
dotnet test --filter-trait Category=Hardware
```

## Project layout

```
src/RfScout/
  Core/              no WPF, no native code: everything here is unit-tested
    Dsp/             FFT, window functions, I/Q conversion
    Spectrum/        sweep block framing, sweep maths, block -> spectrum, full-span assembly
    Streaming/       lock-free ring buffer between the USB callback and the DSP thread
    Display/         colour maps, viewport (zoom/pan) maths
    Fakes/           synthetic radio source used by the tests and the self-test
  Services/HackRf/   libhackrf binding ([LibraryImport], receive-only) and the device layer
  ViewModels/, *.xaml  WPF UI
tests/RfScout.Tests/ unit tests (+ Category=Hardware tests)
native/              x64 native DLLs, the script that builds them, versions and hashes
docs/                design brief, threat model, test plan
```

### How the sweep works

The HackRF streams 16 KB blocks in sweep mode. Each block has a 10-byte header (`0x7F 0x7F` and a
little-endian band-edge frequency) followed by 8187 signed 8-bit I/Q samples captured at 20 Msps.
RfScout reassembles blocks across USB transfers, windows and FFTs each one, and keeps the two
quarters of the spectrum that `hackrf_sweep` keeps (away from the DC spike and the image). It then
places those bins on a full-span grid that is published to the display about ten times a second.
The details, with references to the upstream sources, are in
`src/RfScout/Core/Spectrum/SweepFraming.cs`.

## Native dependencies

`native/x64` holds x64 Release builds of libhackrf and the HackRF tools from
[greatscottgadgets/hackrf](https://github.com/greatscottgadgets/hackrf) **v2026.01.3**, libusb
1.0.30 and pthreads4w 3.0.0 (both from vcpkg). [`native/README.md`](native/README.md) lists the
exact versions and SHA-256 hashes. To rebuild them:

```powershell
pwsh -NoProfile -File native\build-libhackrf.ps1                  # build, collect, print hashes
pwsh -NoProfile -File native\build-libhackrf.ps1 -RunHackrfInfo   # and check the radio answers
```

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | Project setup, native libhackrf build | Done |
| M1 | Wideband sweep, spectrum and waterfall | In progress |
| M2 | Noise floor, peak detection and tracking, band plan, peak table | Planned |
| M3 | Narrowband capture, feature extraction, modulation classifier, SigMF recording | Planned |
| M4 | Device identification: rtl_433 bridge, ADS-B, RDS, AIS | Planned |
| M5 | Locate mode (signal-strength meter) | Planned |
| M6 | Settings, CSV/JSON export, reconnect on unplug, release packaging | Planned |

## Contributing

Issues and pull requests are welcome. Before opening a PR, run the three commands under
[Tests and checks](#tests-and-checks), and add tests for new logic in `Core`. The PR template has
the full checklist. Report security problems privately, as described in [SECURITY.md](SECURITY.md).

## License

No license has been chosen for RfScout's own code yet, so all rights are reserved for now.

The binaries in `native/x64` are third-party software under their own licenses:
libhackrf (BSD-3-Clause) and the HackRF tools (GPL-2.0-or-later), both from
[greatscottgadgets/hackrf v2026.01.3](https://github.com/greatscottgadgets/hackrf/tree/v2026.01.3),
libusb (LGPL-2.1-or-later) and pthreads4w (Apache-2.0).
