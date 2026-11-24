RfScout in one place: the questions people ask before they buy a HackRF, and the ones they ask the first evening they have it plugged in.

## What hardware do I need?
A HackRF One and a 64-bit Windows 10 or 11 PC. RfScout uses the HackRF's hardware sweep mode, which needs firmware that provides USB API 1.02 or later; the app was developed against firmware v2.3.1. Any antenna that covers the band you want to scan works, and an active antenna simply needs **Antenna power** switched on.

## Which driver does the HackRF need?
**WinUSB**, installed once with the HackRF Windows drivers or with [Zadig](https://zadig.akeo.ie/). Without it RfScout cannot open the radio and says so in the status line. The Microsoft Store build needs the same driver, because the Store cannot install drivers for you.

## Does RfScout transmit?
No. RfScout is receive only: the native wrapper exposes no transmit functions at all, and a unit test fails the build if one ever appears. Antenna (bias-tee) power and the 14 dB RF amplifier start off, and switching either on asks for confirmation. Receiving and decoding radio signals is regulated differently from country to country, so make sure your use is legal where you are.

## What does it identify?
The identifiers devices broadcast in the clear: ISM-band sensors and remotes through rtl_433, aircraft through ADS-B (Mode S), FM station names through RDS, and ships through AIS. Everything else gets a best guess from its band, modulation, bandwidth and burst pattern, with the measurements shown beside it. RfScout decodes only public identifiers, never message content, and decrypts nothing.

## What happens to my data?
Nothing leaves your computer. RfScout has no accounts, analytics, telemetry, advertising or crash reporting, and makes no network connections. Settings, I/Q captures and exports stay in `%LOCALAPPDATA%\RfScout` and you can delete them at any time. See the [privacy policy](privacy.html) for the full statement.

## Should I get the Microsoft Store version or the GitHub build?
Both are the same app. The Store build installs and updates itself and runs as a full-trust package; the GitHub build is the plain executable you build yourself or download from the repository. Either way you still install the WinUSB driver once by hand. RfScout is free software under GPL-3.0-or-later, and the [source code](https://github.com/matsura8/RfScout) is the same for both.

## How much CPU and throughput does a full sweep take?
The DSP pipeline sustains well above the radio's 20 Msps sweep rate in a release build (a release-mode gate measures it at about 31.9 Msps), and the display is fed from a lock-free frame buffer so the USB and DSP threads never wait on the UI. The status bar shows sweeps per second, dropped blocks and process CPU while you sweep, so you can watch it yourself.

## Can I use RfScout without a HackRF attached?
Yes. Switch **Source** to **Synthetic** and the app plays a built-in demo signal, so you can learn the interface, the peak table and the analysis pane with no radio on the bus.

## Why does the app say it cannot open my HackRF?
Usually one of three things: the WinUSB driver is missing (run Zadig), another program such as HackRF GUI or `hackrf_transfer` already holds the device, or the cable/port is flaky. Close the other program, check the driver, and press **Start** again. If the radio disappears mid-sweep, a banner appears and the sweep resumes by itself when you replug.

## Why are no peaks showing up?
Peaks need to clear the **Threshold dB** value above the noise floor (10 dB by default). Lower the threshold, raise **LNA** or **VGA** gain, and make sure the sweep is actually running and the span covers the signal you expect.

## Can I change the band names in the peak table?
Yes. The band plan lives in `bandplan.json` beside the app and is editable without rebuilding; the US allocation plan ships as the default.

## Where do I get help?
Open an issue on [GitHub](https://github.com/matsura8/RfScout/issues) or post in the RfScout section of the [support board](../support/default.aspx?section=rfscout). The manual covers every control, and the FAQ answers the rest.