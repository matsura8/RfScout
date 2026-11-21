# Roadmap: RfScout

Derived from the project brief (`C:\workspace\scratch\strata\rfscout-prompt.md`). Each milestone
is finished completely — code, unit tests, `dev lint`, `dev test`, and `dev test -Hardware` when
it touches the radio — and committed before the next one starts.

Status legend: `[ ]` not started · `[~]` in progress · `[x]` done, committed and green.

## M0 — Project setup (done)

- [x] DevFlow csharp template, branch `strata/rfscout`, `dev usb -Add 1d50:6089 -Name hackrf`
- [x] libhackrf + tools built x64 from `hackrf` v2026.01.3, DLLs in `native\x64`, hashes and
      `hackrf_info.exe` output recorded in `native\README.md`
- [x] HackRF bound to WinUSB, `dev usb -Check` passes
- [x] `dev build`, `dev test`, `dev lint` green

## M1 — Wideband sweep and waterfall

Core:

- [x] `SweepMath` — sweep arithmetic, bin layout read from `hackrf_sweep.c` and
      `usb_api_sweep.c` (kept quarters, header = band edge, tuned = header + 7.5 MHz)
- [x] `SweepBlockParser` — reframes blocks across transfer boundaries, counts bad headers
- [x] `FloatFft` + `WindowFunctions` + `IqMath` — radix-2 FFT, Hann, s8 I/Q conversion
- [x] `SweepSpectrumProcessor` — block → dBFS bins, multi-window averaging
- [x] `SpectrumAssembler` + `SpectrumGrid` — full-span grid, peak scatter, double-buffered publish
- [x] `ByteRingBuffer` — SPSC, allocation-free producer path, whole-chunk drops
- [x] `RadioSetup` — gain steps and validation, sweep presets, span planning
- [x] `SelfTest` — hardware-free `--self-test` checks

Device (Services/HackRf):

- [x] `NativeMethods` — `[LibraryImport]` for the RX-only libhackrf surface
- [x] `HackRfDevice` — SafeHandle, RX callback into the ring buffer, sweep + narrowband capture,
      DSP worker thread, `IRadioSource`
- [x] Receive-only test: reflection over `NativeMethods` fails if any TX entry point appears
- [x] Hardware tests: open, board id / firmware / serial, short sweep with plausible power,
      narrowband capture with the expected sample count

UI:

- [x] `MainViewModel` — sweep controls, presets, gains, amp/antenna-power confirmation, status
- [x] `SpectrumPlot` — live trace, peak hold, noise floor, markers, zoom/pan, hover readout
- [x] `WaterfallPlot` — scrolling `WriteableBitmap`, viridis, dB range, frequency axis
- [x] Keyboard: `Space` start/stop, `Ctrl+1..9` presets, `Enter` analyze, `L` locate, `A` analyze,
      `D` listen, `S` save IQ, `F1` About; labelled access keys
- [x] AutomationProperties on both plots plus a live text mirror of the last sweep
- [x] `AboutWindow` — version, device, receive-only and decode policy

Done when: 1–6000 MHz sweep refreshes at least once a second; 88–108 MHz shows FM stations; the
UI stays responsive; a 10-minute run shows no unbounded working-set growth.

## M2 — Peak finding and tracking

- [ ] `NoiseFloorEstimator` — per-bin running low percentile (20th over N sweeps)
- [ ] `PeakDetector` — T dB above floor, neighbour merge, power-weighted centre, SNR,
      occupied bandwidth at -3 dB and -10 dB
- [ ] `PeakTracker` — stable IDs across sweeps, first/last seen, hit count, duty cycle,
      power min/max/avg, short history, continuous / intermittent / bursty class
- [ ] `BandPlan` + `Core/Spectrum/bandplan.json` — US allocations with a region setting, typical
      modulations and the decoders to try
- [ ] Peak table UI: sortable, filterable, cross-highlighted with the plot markers
- [ ] Tests: tones, wide signals, closely spaced pair, sloped floor, bursty signals; peak counts,
      frequency within one bin, bandwidth classes, stable IDs

## M3 — Analyze a peak (narrowband capture and classification)

- [ ] Analyze flow: stop sweep → tune with a filter that suits the peak's bandwidth (2–20 Msps) →
      capture 0.5–2 s → resume sweep; auto-analyze in round-robin
- [ ] `FeatureExtractor` — envelope statistics, instantaneous-frequency histogram, occupied
      bandwidth, burst timing, symbol rate, spectral flatness
- [ ] `ModulationClassifier` — explainable rules → CW / AM / NFM / WFM / OOK / 2-FSK / 4-FSK /
      PSK-like / OFDM-wideband / chirp / unknown, with confidence and reasons
- [ ] `Recording/` — `.cs8` + `.sigmf-meta` under `%LOCALAPPDATA%\RfScout\captures`, size cap,
      oldest-first deletion; small fixtures committed under `tests\fixtures`
- [ ] `FileRadioSource` — plays back a recorded `.cs8`
- [ ] Detail pane: zoomed spectrum, envelope, instantaneous frequency, features, classification
      with reasons, decode log
- [ ] Optional audio (AM/NFM/WFM) with volume
- [ ] Tests: synthetic AM/FM/OOK/FSK classified correctly; live FM broadcast reads WFM

## M4 — Identify devices (decoders)

- [ ] `IDecoder` + `DeviceIdentification` (protocol, device type, id, public fields, confidence)
- [ ] `Rtl433Bridge` — writes `.cs8`, runs `tools\rtl_433\rtl_433.exe -r file -s rate -F json`,
      parses JSON into identifications; version and hash recorded
- [ ] `OokPulseDecoder` — fallback: pulse timing, bit rate, frame length, "unknown protocol"
- [ ] `ModeSDecoder` — 1090 MHz preamble at 2 Msps, CRC, DF17 ICAO / callsign / position
- [ ] `RdsDecoder` — 57 kHz BPSK at 1187.5 baud, block sync and check, PI code and PS name
- [ ] `AisDecoder` — GMSK 9600 baud, NRZI, HDLC, MMSI and ship name (types 1–5, 18, 24)
- [ ] "Best guess" column plus the full identification list in the detail pane
- [ ] Tests on committed fixtures or published vectors (source noted in a comment); live run logs
      what the antenna actually heard

## M5 — Locate

- [ ] Locate mode: stay tuned to one peak, large live S-meter, 60 s strength graph, peak hold
- [ ] Optional audio tone whose pitch follows strength
- [ ] UI states plainly that one HackRF gives frequency and relative strength, not direction

## M6 — Polish and ship

- [ ] Settings per user in `%LOCALAPPDATA%\RfScout\settings.json` (span, gains, threshold, colour
      map, region)
- [ ] Export peak table and identifications to CSV and JSON
- [ ] Status bar: serial, sample rate, sweeps/s, dropped blocks, CPU
- [ ] Unplugged: banner, clean stop, reconnect when the device returns
- [ ] `dev audit` (full) passes; `docs\threat-model.md` filled in (RF data, capture files and
      rtl_433 output are untrusted: bounded parses, size limits)
- [ ] `docs\design-brief.md` and `docs\test-plan.md` completed, including the USB section
- [ ] `dev beta -Quick` run and reported

## Standing rules

1. Receive only: no TX entry point in the native wrapper, enforced by a test.
2. Antenna power and RF amp default off, each behind a confirmation dialog.
3. Decoding is not restricted to broadcast beacons — anything the app can demodulate is decoded;
   encrypted signals are classified because their payload cannot be read. Never decrypt, never
   transmit. README and About box say so.
4. No hard-coded USB ids in tests; `dev test -Hardware` reads `DEVFLOW_USB_DEVICES`.
5. Every change passes `dev lint` and `dev test`; warnings are errors, and no analyzer is
   suppressed without a written justification on the same line.
6. `Core` never references WPF or the native DLL; all DSP and decoding is tested against fakes.
