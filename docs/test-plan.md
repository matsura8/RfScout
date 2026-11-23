# Beta test plan: RfScout

Give each tester this list with the beta kit. Tick and report via the "Beta feedback" issue form.

## Install / first run
- [ ] Unzips and starts on a clean machine (Windows Sandbox kit does this for you)
- [ ] SmartScreen / signature prompt is as expected for a beta (dev-signed)
- [ ] Window title shows the beta version

## Core tasks
- [ ] Task 1 (sweep and see): pick a preset (or type a span), press Space, watch the spectrum and waterfall update; zoom with the wheel and pan by drag; the hover readout follows the cursor.
- [ ] Task 2 (find and identify): with the HackRF attached, start a sweep, wait for the peak table to fill, select a peak, press Enter (Analyze); the detail pane shows modulation class and, for ADS-B/AIS/FM/433 MHz signals, an identification with confidence.
- [ ] Task 3 (chase it): press L on a peak to enter Locate mode, walk with a directional antenna; the strength meter and 60 s graph respond within a second of turning.

## Display and input
- [ ] 100%, 150%, 200% display scaling: no clipped or blurry UI (100% verified in development; 150%/200% are MANUAL checks still outstanding)
- [ ] Light, dark, and a high-contrast theme
- [ ] Keyboard only: Tab order makes sense, Enter/Esc work, focus visible
- [ ] Narrator reads controls sensibly

## USB devices (skip if the app uses none)
- [ ] Driver installed as listed in the beta notes (libusb WinUSB driver for the HackRF; the app also works with no radio via the Synthetic source)
- [ ] Device detected when plugged in before starting the app (status bar shows board name and serial)
- [ ] Hot-plug (MANUAL - could not be done in CI, nobody could pull the plug during development): start a sweep, unplug the HackRF. Expect: sweeping stops cleanly, a banner "The HackRF was unplugged..." appears, status line says No radio: unplugged. Replug within ~2 s of the watcher poll: banner clears, status says HackRF reconnected, and sweeping restarts on its own.
- [ ] Unplug during a transfer (MANUAL): unplug while a capture or analyze is running; expect the operation fails with a status message, no crash, no hang, and the app stays usable
- [ ] Two devices, if supported: only one HackRF is used; with two attached the app opens the first declared one
- [ ] USB hub and direct port; USB 2 and USB 3 ports (the 20 Msps sweep needs USB 2 high-speed or better; on USB 1.1 or a hub sharing bandwidth expect dropped blocks in the status bar)

## Robustness
- [ ] Resize, minimize/restore, move between monitors
- [ ] Bad input (empty, very long, unicode/emoji, paths with spaces)
- [ ] Close while busy; reopen

## Exit criteria for this beta
- No open crash or data-loss bugs
- All core tasks pass for every tester

