# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com), versions: [SemVer](https://semver.org).

## [Unreleased]
### Added
- Sweep pipeline: libhackrf sweep mode, block framing across USB transfers, radix-2 FFT, spectrum assembly on a full-span grid.
- HackRF receive-only device layer with a lock-free ring buffer; a unit test fails if any transmit entry point is bound.
- Display layer: colour maps, dB auto-range, zoom/pan viewport.

## [0.1.0]
### Added
- Project scaffold: WPF app, core library layout, unit tests, CI.



