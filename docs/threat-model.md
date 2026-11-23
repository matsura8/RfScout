# Threat model: RfScout

Update when the app gains a new input, privilege, or external connection. Reviewed before every release.

## What the app touches
| Asset / data | Where it lives | Sensitivity |
|---|---|---|
| Received RF data (spectra, IQ captures, decodes) | RAM; saved SigMF captures in the user's capture folder | Public-by-air identifiers; decoded content may be anything the band carries |
| Per-user settings | %LOCALAPPDATA%\RfScout\settings.json | Low (span, gains, colour map, region) |
| Exports (CSV/JSON) | wherever the user saves them | Peak table + identifications, same sensitivity as decodes |
| libhackrf (native DLL) | loaded in-process | Trusted build, vendored source under native/src |
| rtl_433 (optional external process) | user-installed, invoked on saved capture files | Low: read-only run, output capped and parsed defensively |

## Trust boundaries and inputs
| Input (files, clipboard, command line, network, registry, IPC) | Who controls it | Validation |
|---|---|---|
| RF data (sweep blocks from libhackrf) | the air, via the radio | Fixed 16384-byte block framing with the 0x7F 0x7F header; header frequency sanity-checked; FFT/window sizes fixed at construction |
| Capture files (.cs8 + .sigmf, opened via File > Open) | whoever wrote the file | Size cap: FileRadioSource refuses files over MaxPlaybackBytes (256 MiB); SigMF header parsed with fixed field widths; playback streams, never maps whole file |
| rtl_433 stdout (JSON lines) | the external tool, ultimately the air | Wall-clock timeout; output capped at MaxOutputBytes (4 MiB); at most MaxIdentifications (64) rows; every field capped at MaxFieldLength (96 chars); JSON parsed with MaxDepth 16 |
| settings.json | the user (or a hand-edit) | Size cap 64 KiB; corrupt file quarantined to settings.json.bad and defaults used; every value clamped to the UI range on load (AppSettingsStore.Sanitize) |
| CSV/JSON exports | downstream spreadsheet | Formula-injection guard: cells starting with = + - @ (or hidden behind space/tab/quote) are prefixed with a single quote (CsvBuilder.Escape) |
| Command line | local user | Parsed for a few switches only; unknown arguments ignored |
| DEVFLOW_USB_DEVICES env var | machine operator | Parsed for vid:pid only; unparseable entries skipped; no hard-coded USB ids |

## STRIDE pass
| Threat | Example here | Mitigation | Status |
|---|---|---|---|
| Spoofing | A fake ADS-B preamble sequence claims an aircraft identity | Identifications carry a confidence and source; decoders verify CRC where the protocol has one (Mode S CPR, RDS, AIS parity) | done |
| Tampering | Corrupt settings.json or capture file changes behaviour | Settings: size cap, quarantine-and-default, clamp on load. Captures: size cap, strict header. Both covered by unit tests | done |
| Repudiation | "which capture produced this export?" | Exports carry a UTC timestamp per row and a generated-UTC field in JSON; captures embed gains and timestamp in SigMF | done |
| Information disclosure | Export opened in a spreadsheet executes a formula smuggled over the air | CsvBuilder.Escape prefixes =+-@ cells; README states decodes may carry any band content | done |
| Denial of service | A hostile capture file, rtl_433 flood, or corrupt settings balloons memory | Size limits on every parser (256 MiB capture, 4 MiB rtl_433 output, 64 KiB settings); JSON depth 16; field caps; sweep pipeline uses fixed buffers and a bounded ring buffer | done |
| Elevation of privilege | Runs asInvoker, never requests admin | manifest `requestedExecutionLevel=asInvoker` | done |

## Fuzzing targets
Functions that parse untrusted data: `SigMfMeta.Parse`, `FileRadioSource` open path,
`Rtl433Bridge.ParseJsonLines`, `AppSettingsStore.Load`, `Usb.ParseDeviceList`.
Harness: xunit theory tests with truncated, oversized and malformed inputs (see
tests/RfScout.Tests). SharpFuzz is not wired up; the parsers are pure managed code with
explicit caps, which the theory tests exercise directly.
