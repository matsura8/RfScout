# RfScout (csharp) — agent notes

Native Windows app using DevFlow. Run commands from the repo root with `pwsh -NoProfile -File dev.ps1 <cmd>`
(or `dev <cmd>` if `C:\workspace\devflow\bin` is on PATH).

- Build: `dev build` (add `-Release`, or `-Asan` for C++ AddressSanitizer)
- Test: `dev test` (unit tests, then `<exe> --self-test`). Add tests next to every logic change.
- Before saying work is done: `dev lint` and `dev test` must pass. Warnings are errors.
- Security-relevant change (input parsing, file/registry/network access, process launch, crypto): also run `dev audit -Quick`
  and update docs/threat-model.md.
- USB device code: keep I/O behind an interface with a fake so logic is unit tested; real-device tests follow the
  Hardware convention (C:\workspace\devflow\docs\USB.md). Run `dev usb` then `dev test -Hardware` when the device is attached.
- UI change: follow C:\workspace\devflow\docs\UI-DESIGN.md and tick the UI review checklist in the PR.
- Keep testable logic out of UI code (the `core` module / `Core` folder) so it can be unit tested.
- Generated output goes to out\ (and target\ for Rust). Never commit it.
- Branch `claude/<topic>`; commit with `--author "Claude <claude@apache.local>"` (see C:\workspace\AGENTS.md).
