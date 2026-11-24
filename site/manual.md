RfScout turns a HackRF One into a wideband spectrum scanner: sweep a band, watch the spectrum and waterfall, pick a peak, and find out what it is. Everything below uses the control names as they appear in the window.

## Install and driver setup

RfScout needs a HackRF One and the **WinUSB** driver. Install the driver once before the first run: use the HackRF Windows drivers, or run [Zadig](https://zadig.akeo.ie/), select the HackRF (it may appear as a `libusb` or `HackRF` device, sometimes hidden under *Options > List All Devices*), and pick **WinUSB**. The Store build needs the same driver because the Store cannot install drivers itself.

Plug in the HackRF, start RfScout, and press **Start** (or **Space**). The status bar shows the board name, serial, firmware and USB API version once the radio opens. If the radio cannot be opened, the status line says so and suggests checking that it is plugged in, not in use by another program, and switched to the **Synthetic** source for a demo without a radio. If the HackRF is pulled out mid-sweep, a banner appears and RfScout reconnects and resumes the sweep by itself when you plug it back in.

## Sweeping and presets

Pick a band from the **Preset** drop-down, or type a span into **Start MHz** and **Stop MHz** and pick a **Bin** width. Presets cover the full 1-6000 MHz sweep, FM broadcast, airband, marine VHF/AIS, NOAA weather, amateur HF, VHF, the 433, 868 and 915 MHz ISM bands, 1090 MHz ADS-B and 2.4 GHz; **Ctrl+1** to **Ctrl+9** recall the first nine. Set the receive gains with **LNA** (0-40 dB in 8 dB steps) and **VGA** (0-62 dB in 2 dB steps). The **RF amp** and **Antenna power** check boxes are off by default and ask for confirmation before switching on, because they put 14 dB of amplification or bias-tee power on the antenna port. Press **Apply** to push new span and gain settings to a running sweep. **Source** selects the radio: **HackRf** uses the real device, **Synthetic** plays a built-in demo signal so you can drive the whole UI without a radio attached.

## The spectrum and waterfall displays

The **Spectrum plot** shows the live trace with a peak-hold trace and an estimated noise-floor line; the **Waterfall plot** below it scrolls recent sweeps in colour. Tick **Auto range** to let the dB window follow the signal levels, or set it by hand in the two **dB** boxes and press **Auto now** for a one-time automatic fit. **Clear hold** erases the peak-hold trace, **Reset view** returns the zoom and pan to the full span. The readout above the plot shows the sweep state and the frequency under the pointer.

## Peaks and the peak table

Peaks above the **Threshold dB** value (10 dB by default) appear in the **Peak table** and as markers on the plot; selecting one highlights the other. Columns show the stable tracking **ID**, **Frequency**, **Power**, **SNR**, bandwidth **BW**, **Duty**, **Type** (continuous or bursty), **Band** from the band plan, the **Best guess** at the device, its confidence **Conf**, and **Last seen**. Click a column header to sort, type in **Filter** to narrow the list, and use the arrow keys to move between peaks. Tick **Mute** on a row to grey out an emitter you never want to chase; the muted count shows beside the filter, and Locate refuses a muted peak until you unmute it. The band names come from `bandplan.json`, an editable file you can change without rebuilding.

## Analyze a peak

Select a peak and press **Analy_ze** (or **Enter** in the peak table). RfScout briefly borrows the radio from the sweep, tunes to the peak, captures a short I/Q window, and hands the radio back. The pane shows the classification and its confidence, the reasons and measured features behind the decision, and three plots: **Zoomed spectrum**, **Envelope**, and **Instantaneous frequency**. Tick **Auto-analyze** to analyze each completed sweep automatically. Each capture is saved as a `.cs8` file with SigMF metadata under `%LOCALAPPDATA%\RfScout\captures`, with a size cap and oldest-first clean-up; the saved path appears under the plots. Captures can be replayed through the same analyze and decode paths with the built-in file source.

## Decoders

RfScout reads the identifiers devices broadcast in the clear: ISM-band sensors and remotes are decoded through the bundled [rtl_433](https://github.com/merbanan/rtl_433), aircraft through ADS-B (Mode S), FM station names through RDS, and ships through AIS; anything else gets a best guess from band, modulation, bandwidth and burst pattern. The **Decode log** in the detail pane shows what the decoders found. Decoding is limited to public identifiers, never message content, and nothing is decrypted.

## Recording and playback

Every analyze records its capture automatically as `.cs8` + SigMF metadata in `%LOCALAPPDATA%\RfScout\captures`. The reader is defensive: a truncated file plays the samples it holds, and an oversized file is refused rather than swallowed. Use **Export CSV** or **Export JSON** in the status bar to save the peak table and identifications; the files are written with a timestamp beside the settings file, and the CSV is guarded against spreadsheet formula injection.

## Locate

Select a peak and press **Locate** (or **L**). The sweep stops, the radio stays tuned to the peak in narrowband mode, and the **Locate view** shows a large live **signal strength meter**, a rolling **60 second graph**, a **Peak hold** number with a **Reset** button, and an optional **Audio tone (pitch follows strength)** that is off by default. Press **Exit locate** (or **Esc**) to resume sweeping. A single HackRF gives frequency location and relative signal strength, not direction or position; the Locate view repeats this caveat. Walk a directional antenna toward the transmitter and watch the meter.

## Settings

RfScout saves per-user settings in `%LOCALAPPDATA%\RfScout\settings.json`: the last span and bin width, the gains, the peak threshold, the waterfall colour map, the band-plan region, and the dB window. A missing or corrupt file falls back to the defaults rather than stopping the app. The app follows the Windows light, dark and high-contrast theme.

## Troubleshooting

- **"Could not open the HackRF"**: check it is plugged in and not in use by another program, and that the WinUSB driver is installed (Zadig). Switch **Source** to **Synthetic** to try the UI without a radio.
- **No peaks appear**: lower **Threshold dB**, raise **LNA** or **VGA**, and check the span is wide enough and **Start** was pressed.
- **Everything looks weak**: an active antenna needs **Antenna power** on; a long cable run may justify the **RF amp**. Both ask for confirmation because they affect what the radio sees.
- **The device disappeared**: the unplug banner appears and the sweep resumes automatically on replug; press **Start** if it does not.
- **A muted peak will not locate**: unmute its row in the peak table first.
- **Classification says unknown**: analyze again with a stronger signal or a wider capture; the reasons list shows which measurement was short.