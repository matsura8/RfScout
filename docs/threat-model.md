# Threat model: RfScout

Update when the app gains a new input, privilege, or external connection. Reviewed before every release.

## What the app touches
| Asset / data | Where it lives | Sensitivity |
|---|---|---|
| | | |

## Trust boundaries and inputs
| Input (files, clipboard, command line, network, registry, IPC) | Who controls it | Validation |
|---|---|---|
| Command line | local user | |

## STRIDE pass
| Threat | Example here | Mitigation | Status |
|---|---|---|---|
| Spoofing | | | |
| Tampering | | | |
| Repudiation | | | |
| Information disclosure | | | |
| Denial of service | | | |
| Elevation of privilege | Runs asInvoker, never requests admin | manifest `requestedExecutionLevel=asInvoker` | done |

## Fuzzing targets
Functions that parse untrusted data, and their fuzz harness (cargo-fuzz / libFuzzer / SharpFuzz).
