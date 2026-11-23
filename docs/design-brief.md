# Design brief: RfScout

## Problem
An operator with a HackRF One plugged into a Windows PC wants to answer, in one window and without
any other SDR software: *what is transmitting on this antenna right now, where exactly, how
strong is it, and what kind of device is it?* SDR# shows a tuner; `hackrf_sweep` shows a
sweep but no analysis. RfScout sweeps the whole 1 MHz–6 GHz range, finds the signals worth
looking at, and identifies the devices behind them.

Receive only: RfScout never transmits. Antenna (bias-tee) power is off by default.

## Primary tasks (top 3)
1. **Sweep and see** — start a sweep over a preset or custom span, read the spectrum and
   waterfall, zoom/pan to a suspicious burst.
2. **Find and identify** — read the peak table (frequency, power, SNR, bandwidth, duty
   cycle, band, best guess), select a peak, analyze it, and read the identification
   (aircraft ICAO/callsign, ship MMSI, FM station PS name, 433 MHz sensor model+id).
3. **Chase it** — put the app in Locate mode on one peak and walk around with a directional
   antenna watching the signal-strength meter and 60 s graph.

## Screens / windows
| Window | Purpose | Main controls | Notes |
|---|---|---|---|
| Main | Sweep, analyse, identify, locate | Start/Stop, preset picker, span + bin width, LNA/VGA, Amp, AntPwr, threshold, Auto-analyze; spectrum + waterfall; peak table; selected-peak detail pane; status bar | Single window. Peak table is the keyboard-navigable master list. |
| About (dialog) | Version, device, and the receive-only / decode-policy statement | Close | Opened from the menu; states what is decoded and what is classified only. |
| Confirmation (message box) | Antenna power on, RF amp on | OK/Cancel | Shown before either is enabled; both default off. |

## Look and feel
- Fluent design (Windows 11): system theme (`ThemeMode="System"`), Segoe UI Variable, 4 px
  grid spacing, no custom chrome.
- Keyboard: `Space` start/stop sweep, `←`/`→` (and `↑`/`↓` in the table) move between peaks,
  `Enter` analyze the selected peak, `L` locate, `A` analyze, `D` listen, `S` save IQ,
  `Ctrl+1..9` presets, `F1` About. Every toolbar control has a labelled `_access key`.
- Accessibility: Accessibility Insights FastPass clean; Narrator reads the peak table rows
  (ID, frequency, power, classification) and the status bar; the hand-drawn spectrum and
  waterfall get `AutomationProperties.Name`/`HelpText` plus a live text mirror of the last
  sweep peak, so the information is not graphics-only; readable at 150 % and 200 % scaling.

## Wireframe (Main)
```
+-----------------------------------------------------------------------------------+
| [Start][Stop] Preset v | Start 88.000 MHz  Stop 108.000 MHz  Bin 25 kHz | LNA VGA |
| Amp[ ] AntPwr[ ] | Threshold 10 dB | Auto-analyze [x]                            |
+--------------------------------------------------------------+--------------------+
| Spectrum (live, peak-hold, floor, peak markers)              | Selected peak      |
|--------------------------------------------------------------| 433.92 MHz  -42 dB |
| Waterfall (scrolling, zoom/pan, hover readout)               | OOK, 2.4 kbit/s    |
|                                                              | rtl_433: Acurite   |
|                                                              |  5-in-1 id 1234    |
|                                                              | [Analyze][Locate]  |
|                                                              | [Listen] [Save IQ] |
+--------------------------------------------------------------+--------------------+
| Peak table: ID | Freq | Power | SNR | BW | Duty | Type | Band | Best guess | Conf  |
+-----------------------------------------------------------------------------------+
| HackRF 0000...711B | 20 Msps | 1.4 sweeps/s | dropped 0 | CPU 23%                  |
+-----------------------------------------------------------------------------------+
```
The bottom row is the status bar: device serial, sample rate, sweeps/s, dropped blocks, CPU.
If the HackRF is unplugged a banner replaces it: "HackRF disconnected — Stop pressed, waiting
for the device to come back".

## Data policy
Receive only — the app has no transmitter. Decoding is **not** restricted to broadcast or
beacon identifiers: any signal the app can demodulate is decoded, and the peak table and the
saved SigMF captures may carry whatever that decode produced (ADS-B/Mode S, AIS, FM RDS,
rtl_433-style sensors, and anything else implemented). The only practical limit is
cryptography: encrypted signals (cellular, Wi-Fi/WPA, Bluetooth, digital voice, digital
radio) are classified — band, modulation, bandwidth, burst pattern — because their payload
cannot be read, not because a policy forbids reading it. RfScout never decrypts and never
transmits. The README and About box state this.

## M6 additions (settings, export, unplug, status bar)

- **Settings**: per-user `%LOCALAPPDATA%\RfScout\settings.json` holds the last span, bin width,
  gains, peak threshold, colour map, region and waterfall dB window. A missing, unreadable,
  oversized (>64 KiB) or corrupt file falls back to defaults; the corrupt file is renamed
  `settings.json.bad` once. Values are clamped to the UI ranges on load. Saved on window close,
  loaded at startup.
- **Export**: File > Export writes the peak table and the identification table as CSV
  (two sheets worth of rows, CRLF, RFC 4180) or as one JSON document with both arrays.
  Spreadsheet formula injection is guarded: any cell whose first printable character is
  `=`, `+`, `-` or `@` is prefixed with a single quote, because decoder text (rtl_433 model
  names, RDS PS names, ICAO/callsign strings) arrives over the air.
- **Unplug handling**: an `UnplugMonitor` polls the declared HackRF vid:pid (from the DevFlow
  device declaration, never a hard-coded id) every 2 s. Watching -> Lost fires the banner and
  stops sweeping; Lost -> ReconnectReady clears the banner and restarts; a source that reports
  `Stopped(deviceLost: true)` raises the same banner. The state machine is unit-tested against
  a fake bus probe.
- **Status bar**: device serial (short form), sample rate, sweeps/s, dropped blocks and
  process CPU percent, refreshed on the UI timer.
- **Performance**: the DSP pipeline (s8 IQ -> FFT -> grid -> floor -> peaks -> tracks) is gated
  by a synthetic 20 Msps test; Debug-config runs measure ~6 Msps on the dev laptop, Release
  builds meet the floor (see the M6 report).

## Open questions
- The development antenna is a short whip: weak below 30 MHz. Live identification tests
  log what they see rather than failing on an empty sky.
- rtl_433 is an external process; if it is absent the ISM path degrades to pulse-timing
  classification only.

