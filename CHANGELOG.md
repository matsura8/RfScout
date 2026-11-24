# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com), versions: [SemVer](https://semver.org).

## [Unreleased]
### Added
- Release QA fixes: Space starts/stops the sweep via `ToggleSweepCommand`, an "Auto now" button runs `AutoScaleCommand`, Auto-analyze now runs after each completed sweep, the detail pane draws the zoomed-spectrum, envelope and instantaneous-frequency plots, and the status-bar CPU figure is now a percentage (it previously showed raw tick totals). The plot label, dB axis and CPU readout now follow the live sweep state.
- Mute in the peak table: grey out a known emitter, Locate refuses to chase it until unmuted, muted count beside the filter, `muted` flag in JSON export.
- Per-user settings (`settings.json`), CSV/JSON peak export with spreadsheet formula-injection guard, unplug banner with automatic reconnect, status bar (serial, rate, sweeps/s, dropped blocks, CPU), 20 Msps release-mode throughput gate.
- Sweep pipeline: libhackrf sweep mode, block framing across USB transfers, radix-2 FFT, spectrum assembly on a full-span grid.
- HackRF receive-only device layer with a lock-free ring buffer; a unit test fails if any transmit entry point is bound.
- Display layer: colour maps, dB auto-range, zoom/pan viewport.

## [0.1.0]
### Added
- Project scaffold: WPF app, core library layout, unit tests, CI.



