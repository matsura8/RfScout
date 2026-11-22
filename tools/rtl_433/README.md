# rtl_433 (official Windows x64 release)

Kept here for RfScout M4 `Rtl433Bridge` (ISM 315/433/868/915 MHz OOK/FSK telemetry).

- Source: https://github.com/merbanan/rtl_433/releases (official releases only; no unofficial builds)
- Asset: `rtl_433-win-x64-25.12.zip`
- Version: 25.12 (banner: `rtl_433 version 25.12 branch  at 202512121350 inputs file rtl_tcp RTL-SDR SoapySDR`)
- Downloaded: 2026-11-22 from https://github.com/merbanan/rtl_433/releases/download/25.12/rtl_433-win-x64-25.12.zip
- SHA-256 of the zip: ed42651d2a2a94f6419f7675955bd65449d8247e27822c65c363595b715b7de6
- SHA-256 of rtl_433.exe: d66ab80253889b62d1dfcab2af94f1186cb86b88696b9c0f3dc67da62933026a
- SHA-256 of librtlsdr.dll: c5453cbc65ceb8e76f6a938f46fc4b76c5abdea8dcb2350f4774612547cd9c2a

Usage (Rtl433Bridge): `rtl_433.exe -f <MHz> -s <rate> -r <file.cs8> -F json -q` with a 20 s
timeout and a 4 MiB output cap; its JSON is treated as untrusted input. When the binary is
missing the bridge reports that and the pipeline falls back to `OokPulseDecoder`.
