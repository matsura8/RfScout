# Third-party notices

RfScout is licensed under the GNU General Public License, version 3 or (at your option) any later
version; see [LICENSE](LICENSE). It ships or invokes the third-party components below, each under
its own license. All of them are compatible with GPL-3.0-or-later; pthreads4w's Apache-2.0 license
is the reason RfScout uses version 3 of the GPL rather than version 2.

| Component | Version | Files | License | Source |
|---|---|---|---|---|
| libhackrf (Great Scott Gadgets) | 0.9.2, hackrf v2026.01.3 (`1cfe7dfe`) | `native/x64/hackrf.dll`, `native/x64/hackrf.h` | BSD-3-Clause | https://github.com/greatscottgadgets/hackrf/tree/v2026.01.3/host/libhackrf |
| hackrf-tools (Great Scott Gadgets) | hackrf v2026.01.3 (`1cfe7dfe`) | `native/x64/hackrf_*.exe` (development checks; not part of the app) | GPL-2.0-or-later | https://github.com/greatscottgadgets/hackrf/tree/v2026.01.3/host/hackrf-tools |
| libusb | 1.0.30 (vcpkg `x64-windows`) | `native/x64/libusb-1.0.dll` | LGPL-2.1-or-later | https://github.com/libusb/libusb/releases/tag/v1.0.30 |
| pthreads4w | 3.0.0 (vcpkg `x64-windows`, port revision 14) | `native/x64/pthreadVC3.dll`, `pthreadVCE3.dll`, `pthreadVSE3.dll` | Apache-2.0 | https://sourceforge.net/projects/pthreads4w/ |
| rtl_433 | 25.12 (official Windows x64 release) | `tools/rtl_433/rtl_433.exe`, `rtl_433_64bit_static.exe` | GPL-2.0-or-later | https://github.com/merbanan/rtl_433/tree/25.12 |
| librtlsdr (bundled with rtl_433) | as shipped in rtl_433 25.12 | `tools/rtl_433/librtlsdr.dll` | GPL-2.0-or-later | https://github.com/osmocom/rtl-sdr |
| SoapySDR (bundled with rtl_433) | as shipped in rtl_433 25.12 | `tools/rtl_433/SoapySDR.dll` | BSL-1.0 | https://github.com/pothosware/SoapySDR |
| libusb (bundled with rtl_433) | as shipped in rtl_433 25.12 | `tools/rtl_433/libusb-1.0.dll` | LGPL-2.1-or-later | https://github.com/libusb/libusb |
| .NET runtime and libraries | 10.0 (bundled in self-contained builds) | `RfScout.exe` (single-file) | MIT | https://github.com/dotnet/runtime |
| System.Management | 11.0.0 (NuGet) | `RfScout.exe` (single-file) | MIT | https://github.com/dotnet/runtime |

## Source code for the GPL and LGPL components

The GPL-2.0-or-later and LGPL-2.1-or-later binaries above are unmodified builds of the exact upstream
versions listed. Their complete corresponding source is at the links in the table (tagged releases),
and `native/build-libhackrf.ps1` rebuilds `hackrf.dll`, the hackrf tools, libusb and pthreads4w from
source. If a link stops working, open an issue on this repository and the source will be provided
for at least three years from the release that shipped the binary.

The LGPL-2.1 libraries are separate DLLs that RfScout loads at run time, so you can replace them with
your own builds.

## License texts

- GPL-3.0: [LICENSE](LICENSE)
- GPL-2.0: https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt
- LGPL-2.1: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0.txt
- BSD-3-Clause (libhackrf): https://github.com/greatscottgadgets/hackrf/blob/v2026.01.3/COPYING
- BSL-1.0: https://www.boost.org/LICENSE_1_0.txt
- MIT (.NET): https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
