# Beta test plan: RfScout

Give each tester this list with the beta kit. Tick and report via the "Beta feedback" issue form.

## Install / first run
- [ ] Unzips and starts on a clean machine (Windows Sandbox kit does this for you)
- [ ] SmartScreen / signature prompt is as expected for a beta (dev-signed)
- [ ] Window title shows the beta version

## Core tasks
- [ ] Task 1:
- [ ] Task 2:
- [ ] Task 3:

## Display and input
- [ ] 100%, 150%, 200% display scaling: no clipped or blurry UI
- [ ] Light, dark, and a high-contrast theme
- [ ] Keyboard only: Tab order makes sense, Enter/Esc work, focus visible
- [ ] Narrator reads controls sensibly

## USB devices (skip if the app uses none)
- [ ] Driver installed as listed in the beta notes (e.g. WinUSB via Zadig)
- [ ] Device detected when plugged in before starting the app
- [ ] Hot-plug: unplug and replug while running, and the app recovers or shows a clear message
- [ ] Unplug during a transfer: no crash or hang
- [ ] Two devices, if supported
- [ ] USB hub and direct port; USB 2 and USB 3 ports

## Robustness
- [ ] Resize, minimize/restore, move between monitors
- [ ] Bad input (empty, very long, unicode/emoji, paths with spaces)
- [ ] Close while busy; reopen

## Exit criteria for this beta
- No open crash or data-loss bugs
- All core tasks pass for every tester
