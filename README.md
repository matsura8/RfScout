# RfScout

**A receive-only HackRF One spectrum scanner for Windows.** RfScout sweeps up to 6 GHz, shows a live
spectrum and waterfall, finds and tracks the signals worth looking at, and works out what kind of
device is behind each one.

> **Status: feature complete.** Milestones 1–6 are all merged: the wideband sweep with live
> spectrum and waterfall, peak detection and tracking with a peak table, narrowband analysis with
> a modulation classifier and SigMF recording, device identification (ADS-B, AIS, RDS, rtl_433
> sensors), Locate mode, and per-user settings with CSV/JSON export and unplug/replug recovery.
> A few checks are still manual or synthetic-only, and some test cases are skipped — see
> [Known limitations](#known-limitations) before judging what has been proven on real hardware.

## What it does

- **Wideband sweep** *(done)*. It uses the HackRF's hardware sweep mode (the same mode
  `hackrf_sweep` uses) to cover 1 MHz–6 GHz, with presets for common bands: full range, FM
  broadcast, airband, marine VHF/AIS, NOAA weather, amateur HF, VHF and the 433, 868 and 915 MHz
  ISM bands. LNA gain (0–40 dB in 8 dB steps) and VGA gain (0–62 dB in 2 dB steps) are adjustable.
- **Spectrum and waterfall** *(done)*. A live trace with peak hold and an estimated noise floor,
  plus a scrolling, colour-mapped waterfall with automatic or manual dB range. Both are drawn by a
  custom DPI-aware control fed from a lock-free frame buffer, so the USB and DSP threads never
  wait on the UI.
- **Find and track peaks** *(done)*. A per-bin rolling-percentile noise floor follows gain and
  frequency changes. Peaks above an adjustable threshold (10 dB by default) get a power-weighted
  centre frequency, SNR, −3 dB and −10 dB bandwidth and a bandwidth class. Each one is then
  tracked across sweeps with a stable ID, duty cycle, power history and a continuous or bursty
  label. A band plan in `bandplan.json` (US allocations, editable without rebuilding) names the
  band each peak sits in. The peak table and the markers on the plot select each other.
  Known emitters you never want to chase (a pager base, a broadcast repeater) can be muted from
  the table: the row stays, greyed out, and Locate skips it until you unmute it.
- **Analyse a peak** *(done, live checks pending)*. RfScout borrows the radio from the running
  sweep, tunes to the peak with a suitable sample rate and filter, captures a short I/Q window and
  measures it: envelope statistics, an instantaneous-frequency histogram, occupied bandwidth,
  burst timing and spectral flatness. A rule-based classifier labels it AM, NFM, WFM, OOK, FSK and
  so on, with a confidence and the measurements behind the decision. Captures are saved as `.cs8`
  with SigMF metadata under `%LOCALAPPDATA%\RfScout\captures`, with a size cap and oldest-first
  clean-up, and can be played back.
- **Identify devices** *(done, live checks partly pending)*. It decodes the public identifiers devices broadcast:
  ISM-band sensors and remotes (through [rtl_433](https://github.com/merbanan/rtl_433)), aircraft
  ADS-B (Mode S), FM station names (RDS) and ship AIS. Anything else is classified by band,
  modulation, bandwidth and burst pattern.
- **Locate** *(done)*. Stay tuned to a chosen peak in narrowband mode and watch a large live
  signal-strength meter, a rolling 60-second strength graph and a peak-hold number, with an
  optional audio tone whose pitch rises with strength (off by default). Built for walking a
  directional antenna toward a transmitter. **A single HackRF gives frequency location and
  relative signal strength, not direction or position** - the Locate view says so too.

### Receive only

RfScout never transmits. The native wrapper exposes no transmit functions, and a unit test fails
the build if one appears. Antenna (bias-tee) power and the RF amplifier start **off**, and turning
either on asks for confirmation. RfScout decodes only public broadcast identifiers, never message
content, and never decrypts anything.

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

### Using it

1. Plug in the HackRF and start RfScout.
2. Pick a band preset (or set start, stop and bin width) and the gains, then press **Start**.
3. Watch the spectrum and waterfall. Peaks above the threshold appear in the peak table and as
   markers on the plot. Selecting either highlights the other.
4. Select a peak to analyse it. The detail pane shows the classification, its confidence and the
   measurements behind it, and the capture is saved for later playback.

## Tests and checks

Tests use xUnit v3 on Microsoft.Testing.Platform (see `global.json`). The DSP, peak, classifier,
recording and display code is tested against synthetic signals, so no radio is needed. The suite currently has 302 tests (5 skipped with documented reasons).

```powershell
dotnet test --filter-not-trait Category=Hardware   # unit tests, no radio needed
dotnet format --verify-no-changes                  # formatting (.editorconfig)
dotnet build -c Release -warnaserror               # analyzers, including the security rules, as errors
```

CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs these steps plus the
`--self-test` smoke run on every push and pull request. CI installs the latest .NET 10 SDK, which
can report analyzer warnings that an older local SDK does not. If CI fails where your local build
passes, update your SDK.

**Hardware tests** need a HackRF plugged in. They log what they see on the air rather than failing
on a quiet band.

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
    Spectrum/        sweep framing and maths, noise floor, peak detector, peak tracker,
                     band plan (bandplan.json)
    Analysis/        analyze controller, feature extractor, modulation classifier, capture plots
    Recording/       .cs8 + SigMF capture writer and reader, file playback source
    Streaming/       lock-free buffers between the USB callback, the DSP thread and the UI
    Display/         colour maps, viewport (zoom/pan) maths, spectrum and waterfall models,
                     peak markers
    Fakes/           synthetic radio source used by the tests and the self-test
  Controls/          SpectrumGrid: the custom spectrum and waterfall control
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
places those bins on a full-span grid. The grid accumulates over a pass and is published to the
display about ten times a second. The details, with references to the upstream sources, are in
`src/RfScout/Core/Spectrum/SweepFraming.cs`.

### How a peak is analysed

Analysis interrupts the sweep only briefly: it borrows the open radio, retunes to the peak,
captures the I/Q window and hands the radio back. The feature extractor turns the capture
into measurements, and the classifier applies explainable rules to them. There is no machine
learning, so every label comes with the features that decided it.

## Known limitations

- **Live hardware checks are partly outstanding.** The sweep and peak pipeline have been run on a
  HackRF, but the narrowband capture and live FM classification tests have not yet been re-run
  since analysis mode was added. A long-running memory soak test has not been run either.
- **One classifier case is skipped.** A synthetic 2-FSK signal (5 kHz deviation, 4800 baud) is
  classified as NFM. Further tuning is planned.
- **Two Mode S frame tests are skipped.** At 2 Msps the test builder's half-microsecond pulses are
  a single sample and the decoder's edge timing cannot resolve them; the callsign and airborne
  position frames decode only after a proper pulse resampler, which is deferred. The CRC and ICAO
  paths are still covered.
- **The 20 Msps performance test is skipped in debug builds.** The DSP pipeline sustains about
  6 Msps when the test runs against a debug build of the app; measured in a release build it
  sustains 31.9 Msps, above the 20 Msps real-time floor, and the test passes when run in
  release. The unplug-and-replug check is likewise
  written up as a manual test — nobody could pull the plug during development.
- **Display gaps.** There is no cursor readout or marker overlay on the plot yet. Mouse zoom and
  pan exist in the viewport model but are not yet wired to the control. The waterfall palette
  cannot be changed from the UI.
- **No audio.** Listening to AM/FM signals is planned but not implemented.

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | Project setup, native libhackrf build | Done |
| M1 | Wideband sweep, spectrum and waterfall | Done |
| M2 | Noise floor, peak detection and tracking, band plan, peak table | Done |
| M3 | Narrowband capture, feature extraction, modulation classifier, SigMF recording | Done (live checks pending) |
| M4 | Device identification: rtl_433 bridge, ADS-B, RDS, AIS | Done |
| M5 | Locate mode (signal-strength meter) | Done |
| M6 | Settings, CSV/JSON export, reconnect on unplug, docs, audit | Done |

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



