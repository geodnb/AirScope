# AirScope

**A fast, modern, read-only Wi-Fi analyser for Windows 10 and 11.**

<p>
  <a href="https://github.com/geodnb/AirScope/releases/latest/download/AirScope.exe"><img alt="Download AirScope.exe" src="https://img.shields.io/badge/Download-AirScope.exe-3DD6C6?style=for-the-badge&logo=windows&logoColor=white"></a>
  <a href="https://github.com/geodnb/AirScope/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/geodnb/AirScope?style=for-the-badge&label=Latest&color=2A313C"></a>
  <a href="LICENSE.md"><img alt="Licence: PolyForm Noncommercial" src="https://img.shields.io/badge/Licence-PolyForm%20Noncommercial-2A313C?style=for-the-badge"></a>
</p>

> **[⬇ Download AirScope.exe](https://github.com/geodnb/AirScope/releases/latest/download/AirScope.exe)** — single portable file, no installer, no admin rights. &nbsp;·&nbsp; [All releases](https://github.com/geodnb/AirScope/releases)

AirScope shows you what your Wi-Fi is really doing: a live, smooth signal graph for your own connection, every nearby network with its true channel width and Wi-Fi generation, a channel advisor that understands UK rules, and timed spot tests for fair before/after comparisons.

It was built as a better alternative to the WiFi Analyzer app commonly used on Windows, which can feel laggy, basic and unreliable.

## Highlights

- **Live signal up to 10 times a second**, read straight from your adapter's driver, on a graph that scrolls smoothly in real time.
- **True channel widths** (20 / 40 / 80 / 160 / 320 MHz) for every nearby network, so you can see real overlap.
- **Wi-Fi 4, 5, 6, 6E and 7** detection, including Wi-Fi 7 multi-link (MLO).
- **Channel advisor** that scores every channel block and follows UK/EU rules.
- **Roam, disconnect and DFS radar event detection.**
- **Spot tests** for before/after comparisons (antennas, router position, channel changes).
- **Mini mode**: a small always-on-top signal monitor.
- **CSV recording** of everything it sees.
- **No admin rights, no internet access, no telemetry, no changes to your PC.**

<img width="1660" height="1056" alt="Screenshot 2026-10-29 032707" src="https://github.com/user-attachments/assets/623eafc6-0157-488f-9b84-1b7035c1f9fb" />


---

## Contents

- [Highlights](#highlights)
- [Features](#features)
- [How it improves on the usual Windows Wi-Fi analyser](#how-it-improves-on-the-usual-windows-wi-fi-analyser)
- [Getting started](#getting-started)
- [Settings](#settings)
- [Files AirScope creates](#files-airscope-creates)
- [Accuracy and limitations](#accuracy-and-limitations)
- [Privacy and safety](#privacy-and-safety)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Disclaimer](#disclaimer)
---

## Features

### Live

Your current connection, in real time.

- **Big signal readout** in dBm, colour-coded from Excellent to Unusable, with a quality bar.
- **Live signal graph** that scrolls continuously instead of jumping once per update. Choose a window of 30 seconds, 2, 5, 15 or 30 minutes.
- **Link rates**: transmit and receive speeds drawn on a second axis, so you can see how speed tracks signal.
- **Hover** anywhere on the graph to read the exact signal, rates and time at that moment. **Pause** freezes the graph so you can inspect it.
- **Long windows stay honest.** When zoomed out, a faint shaded band shows the minimum and maximum in each slice, so brief drops aren't averaged away.
- **Signal statistics** over the chosen window: minimum, average, maximum and jitter (how much the signal wobbles), with a plain-English verdict.
- **Link details**: network, access point address (BSSID) and manufacturer, band and channel (with DFS flagged), channel width, Wi-Fi standard, security and encryption, protected management frames, the access point's theoretical top speed and antenna streams, retry rate, client count and channel busyness (when the router advertises them), access point uptime, BSS colour, country code, and how long you've been connected.

### Events

A running log of anything notable on your connection:

- **Roams**: when your PC moves from one access point to another (for example in a mesh system), with old and new channels.
- **Disconnects and reconnects**, including how long you were offline.
- **Brief drops** that recovered on their own.
- **Sudden signal drops** of 10 dB or more.
- **Link-rate collapses** that persist, as opposed to normal rate adjustment.
- **Channel changes** by your router. If it leaves a DFS channel, AirScope notes it may have been a **radar event**, which forces routers off those channels.

Short blips are filtered out, so a normal roam isn't logged as a disconnect followed by a reconnect.

### Networks

Every access point AirScope can see, in a sortable live table.

| Column | What it shows |
|---|---|
| Network | Name, colour-coded consistently across every view. ● marks your connection. |
| Signal | Strength in dBm with a bar meter. |
| Ch / Band / Width | Primary channel, band (2.4, 5 or 6 GHz), and the full width it occupies. |
| Gen | Wi-Fi 4, 5, 6 or 7. |
| Security | Open, WEP, WPA2/WPA3-Personal, WPA2/WPA3-Enterprise, Enhanced Open (OWE). |
| MIMO | Number of antenna streams the access point supports (for example 2×2, 4×4). |
| Clients / Busy | Connected devices and how busy the channel is, when the router advertises it. |
| AP max | The access point's theoretical top speed. |
| Vendor | Manufacturer, from a built-in offline database. |
| Flags | MLO (Wi-Fi 7 multi-link), WPS, PMF, Hidden. |

- **Filter** by name, address, manufacturer, security or channel number.
- **Band toggles** for 2.4, 5 and 6 GHz.
- **One row per SSID** collapses a network's multiple access points into its strongest one.
- **Out-of-range networks** fade out instead of vanishing immediately.
- **Details panel** for the selected network, with a signal history sparkline and every decoded field.
- Rows update in place, so your sort order and selection stay put as scans come in.

### Channels

Spectrum-style graphs for 2.4, 5 and 6 GHz.

- Each network is drawn across the **full width it actually occupies**, with height equal to signal strength, so overlap is obvious at a glance.
- **Your connection** is outlined in white.
- **Shaded regions** mark DFS channels, weather-radar channels, the unused gap between 5 GHz bands, and the part of 6 GHz not authorised for Wi-Fi in the UK.
- **Hover** for details of every network under the cursor; **click** to select it in the Networks table.
- Selecting networks in the table highlights them here and dims the rest.

### History

Signal over time for several networks at once, over 5, 10 or 30 minutes.

- **Selected networks**: whatever you picked in the Networks table.
- **Strongest 10**: the loudest networks around you.
- **My network (all APs)**: every access point broadcasting your network name, which is handy for mesh systems and dual-band routers.

### Advisor

Recommends the cleanest channels for your router.

- Scores every standard channel block on each band:
  - **2.4 GHz:** channels 1, 6, 11 and 13
  - **5 GHz:** 40, 80 and 160 MHz blocks
  - **6 GHz:** 80, 160 and 320 MHz blocks within the UK lower 6 GHz band
- The score (0–100) reflects how much neighbour signal overlaps each block, weighted by how strong those neighbours are and how busy they report being. 100 is clean air.
- Tells you **which block you're on**, which is **best**, and whether moving is **actually worth it**. It won't suggest a change for a marginal gain.
- **Follows UK/EU rules.** For example, it won't recommend 5 GHz channel 144, which isn't permitted in the UK. It also notes DFS start-up delays, weather-radar channels and indoor-only bands.
- Can **ignore your own router's other networks** so they don't count against you.

### Spot tests

Fair, repeatable measurements for comparing setups.

1. Type a label (for example "Stock antennas, desk").
2. Choose a length: 10 seconds, 30 seconds, 1 minute or 2 minutes.
3. Press **Start test** and keep the PC still.

Each result records average, minimum and maximum signal, jitter, average transmit and receive rates, retry rate and roams. Results are saved between sessions. Mark one as the **baseline** (★) and every other run shows its difference in dB and transmit rate. Export everything to CSV.

Ideal for testing antenna upgrades, router placement, channel or band changes, and mesh node positions.

### Mini mode

A compact floating window showing live signal, quality, transmit/receive rates, network and channel, with a 60-second sparkline.

- Stays on top by default (toggle in Settings or by right-clicking it).
- Drag it anywhere; it remembers its position.
- Double-click it, or press ✕, to return to the full window.

Handy on a second monitor or while gaming.

### Recording

Press **Record** to log every live sample and every scan to CSV files in `Documents\AirScope\Logs`. The files open in Excel or any spreadsheet. Network names are escaped so they can't be interpreted as spreadsheet formulas.

---

## How it improves on the usual Windows Wi-Fi analyser

| | AirScope |
|---|---|
| **Responsiveness** | Your own signal is polled directly from the driver up to 10 times a second, separately from neighbour scanning, so it never waits on a scan. The graph scrolls smoothly in real time rather than stepping. |
| **Scan timing** | Waits for Windows to say a scan has finished, then reads the fresh results, rather than re-reading a stale cache on a timer. |
| **Channel width** | Decodes the real width each network uses, up to 320 MHz on Wi-Fi 7, and draws it to scale. |
| **Modern Wi-Fi** | Recognises Wi-Fi 6, 6E and 7, multi-link (MLO), WPA3, Enhanced Open and protected management frames. |
| **Extra detail** | Antenna streams, client count, channel busyness, access point uptime, BSS colour and theoretical top speed. |
| **Diagnostics** | Event log for roams, drops, link-rate collapses and DFS radar channel changes. |
| **Recommendations** | A channel advisor that understands UK/EU regulations and tells you whether a change is worthwhile. |
| **Comparisons** | Spot tests with a baseline and differences, for genuine before/after testing. |
| **Interface** | Modern dark interface, sortable and filterable table, hover inspection, synchronised selection across views, and a mini mode. |
| **Data export** | Full CSV recording of live samples and scans, plus spot-test export. |
| **Footprint** | One portable exe. No installer, no account, no ads, no internet access. |

---

## Getting started

1. [Download `AirScope.exe`](https://github.com/geodnb/AirScope/releases/latest/download/AirScope.exe) from the [latest release](https://github.com/geodnb/AirScope/releases/latest) and put it in any folder you like.
2. Double-click it. No installation or admin rights are needed.
3. **First run:** because the exe isn't code-signed, Windows SmartScreen may warn you. Click **More info**, then **Run anyway**.
4. If you have more than one Wi-Fi adapter, choose the one you want from the drop-down at the top.

### Windows 11 location permission

Since Windows 11 version 24H2, apps need Location access to see nearby Wi-Fi networks. If AirScope shows a yellow banner:

1. Click **Open Location settings** in the banner.
2. Turn on **Location services**.
3. Turn on **Let desktop apps access your location**.

AirScope does not use or store your location. Windows simply groups Wi-Fi scanning under this permission because nearby network names can reveal where you are. Your own connection's live signal works without it.

---

## Settings

Open with the ⚙ button.

| Setting | Options | Notes |
|---|---|---|
| Live poll rate | 100 ms, 250 ms (default), 500 ms, 1 s | How often your own signal is read. Faster is smoother; it has negligible cost. |
| Neighbour scan interval | 3 – 60 s (default 10 s) | Each scan briefly takes your adapter off-channel. Short intervals can cause small latency spikes while gaming. |
| Mini window stays on top | On / Off | |

Settings are saved automatically.

---

## Files AirScope creates

| Location | Contents |
|---|---|
| `%AppData%\AirScope\settings.json` | Your settings. |
| `%AppData%\AirScope\spot-tests.json` | Saved spot-test results. |
| `%AppData%\AirScope\error.log` | Only created if something goes wrong. Useful for bug reports. |
| `Documents\AirScope\Logs\` | CSV recordings, only when you press Record. |

To remove AirScope completely, delete the exe and these two folders.

---

## Accuracy and limitations

- **Signal readings come from your adapter's driver.** Different adapters and drivers can read several dB apart, so compare spot tests on the same PC and adapter.
- **Neighbour networks update at the scan interval**, typically every few seconds at best. That's a limit of Wi-Fi hardware and Windows, not AirScope. Only your own connection updates in real time.
- **"AP max" is theoretical.** It's calculated from what the access point advertises (generation × channel width × streams). Your actual link speed is also limited by your own adapter, distance and interference, and real-world throughput is much lower than the link rate.
- **Noise floor and SNR aren't available.** Windows doesn't expose them to apps.
- **Clients and channel busyness** only appear when the router includes that information in its beacons. Many home routers don't.
- **Retry rate** depends on the driver reporting frame statistics. Some don't, in which case it shows "not reported by driver".
- **The channel advisor is a guide, not a guarantee.** It only sees networks your PC can hear from where it sits, not what your router hears, and it can't detect non-Wi-Fi interference (microwaves, Bluetooth, baby monitors and so on).
- **Regulatory information reflects UK rules as of late 2026.** Rules change. Ofcom, for example, is consulting on opening more of the 6 GHz band. Always make sure your router's settings comply with the regulations where you are.
- **Vendor names** come from the public IEEE registry. Routers often use "virtual" addresses for guest or mesh networks; AirScope makes a best guess and labels these as virtual.

---

## Privacy and safety

- **Read-only.** AirScope never changes Windows settings, the registry, services, drivers, or your adapter or router configuration. The only thing it asks your adapter to do is scan for networks, which Windows does in the background anyway.
- **No admin rights.** It runs with standard user permissions.
- **No internet or network access.** It makes no connections of any kind, has no telemetry, analytics or update checks, and has no accounts.
- **No third-party packages.** The manufacturer database is built in.
- **Your data stays on your PC.** Nothing is sent anywhere. Files are only written to the locations listed above.
- **Hardened against hostile networks.** Network names and beacon data come from any device nearby, so AirScope treats them as untrusted. Parsing is bounds-checked and was tested against tens of thousands of malformed inputs. Invisible and direction-changing characters in network names are neutralised, so a nearby network can't disguise its name as yours.
- **DLL safety.** System libraries are loaded only from the Windows System32 folder.

---

## Troubleshooting

| Problem | What to try |
|---|---|
| Yellow "Windows is hiding nearby networks" banner | Enable Location access. See [Windows 11 location permission](#windows-11-location-permission). |
| "No Wi-Fi adapter found" | Check the adapter is enabled in Device Manager and Airplane mode is off. |
| "WLAN AutoConfig service isn't running" | Open **Services**, find **WLAN AutoConfig**, start it and set it to Automatic. |
| SmartScreen blocks the exe | Click **More info**, then **Run anyway**. |
| Can't replace the exe with a new version | AirScope may still be running. Close it, or end it in Task Manager. |
| Few or no neighbour networks | Press **Scan now**, and check the scan interval. Some adapters return fewer results while busy. |
| Something else goes wrong | AirScope shows the cause and saves details to `%AppData%\AirScope\error.log`. Include that file when reporting a problem. |

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/geodnb/AirScope.git
cd AirScope\src

# Run from source
dotnet run -c Release

# Build a single, self-contained AirScope.exe (no .NET install needed to run it)
dotnet publish -c Release -r win-x64
# Output: src\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AirScope.exe
```

**Project layout**

| Folder | Contents |
|---|---|
| `src/Interop/` | Calls into the Windows Native Wi-Fi API. |
| `src/Core/` | Monitoring engine, beacon parser, channel maths, advisor, logging, spot tests, settings. |
| `src/UI/` | Custom-drawn graphs and controls. |
| `src/Themes/` | Dark theme. |
| `src/Resources/` | Compressed IEEE manufacturer database. |
| `src/*.xaml` | Main and mini windows. |

---

## Disclaimer

AirScope is provided **"as is", without warranty of any kind**, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose and non-infringement.

- **Use at your own risk.** In no event shall the author or contributors be liable for any claim, damages or other liability, whether in contract, tort or otherwise, arising from, out of or in connection with the software or its use. This includes, without limitation, loss of data, loss of connectivity, hardware or software issues, or any decisions made based on its readings.
- **Not professional advice.** AirScope's readings, scores and recommendations are informational estimates. They are not a substitute for a professional site survey, specialist test equipment, or advice from your internet provider or a qualified installer.
- **Regulatory compliance is your responsibility.** Channel and band information is provided for guidance only and may be incomplete or out of date. Before changing your router's channel, width, power or band settings, make sure the configuration is permitted where you live. In the UK this is set by Ofcom.
- **Only analyse networks you're entitled to.** AirScope passively lists information that nearby networks broadcast publicly, in the same way Windows does when you open the Wi-Fi menu. It does not and cannot connect to, intercept traffic from, or attack other networks. Do not use it, or any information it provides, to attempt unauthorised access to networks you don't own or have permission to use. Doing so may be a criminal offence, for example under the UK Computer Misuse Act 1990.
- **Third-party names.** Wi-Fi®, Wi-Fi 6E and Wi-Fi 7 are trademarks of the Wi-Fi Alliance. Windows is a trademark of Microsoft Corporation. Manufacturer names come from the public IEEE registry. All other trademarks belong to their respective owners. AirScope is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft, the Wi-Fi Alliance, the IEEE, or the developers of any other Wi-Fi analyser.

---

## Licence

AirScope is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md).

You're free to use, study, modify and share it for **non-commercial** purposes. You may **not** sell it, or use it or derivatives of it commercially, without permission.

Required Notice: Copyright (C) 2026 geodnb (https://github.com/geodnb/AirScope)
