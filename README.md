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
- **Gaming-friendly scanning**: by default AirScope only scans for neighbours while you're looking at them, so it won't cause lag spikes while you play.
- **Scan-aware graphs**: scan periods are shaded, so you can tell a real signal problem from a scan glitch.
- **Optional router ping**: see real latency and packet loss, and whether each spike was caused by a scan.
- **Screenshot mode**: hides network names and router addresses on screen, so you can share screenshots safely.
- **Mini mode**: a small always-on-top signal monitor.
- **CSV recording** of everything it sees.
- **No admin rights, no internet access, no telemetry, no changes to your PC.**



---

## Contents

- [Highlights](#highlights)
- [What's new](#whats-new)
- [Features](#features)
- [How it improves on the usual Windows Wi-Fi analyser](#how-it-improves-on-the-usual-windows-wi-fi-analyser)
- [Getting started](#getting-started)
- [Settings](#settings)
- [Scans and gaming](#scans-and-gaming)
- [Files AirScope creates](#files-airscope-creates)
- [Accuracy and limitations](#accuracy-and-limitations)
- [Privacy and safety](#privacy-and-safety)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Disclaimer](#disclaimer)
---

## What's new

### 1.2

- **Clear button** on the live graph: starts the graph and its signal, retry and ping statistics afresh. Handy before a speed test or an antenna change. Events and recordings are kept.
- **Retries that make sense on an idle PC.** When your PC is barely using the network, a couple of retried frames can look like 30–40%. AirScope now shows "≈x% · light traffic" when there's too little traffic for a reliable figure, only counts busy seconds towards the peak, and weights spot-test retries by traffic. Hover over the graph to see how many frames each reading is based on.
- **Screenshot mode** (Settings ⚙): replaces network names and router addresses everywhere on screen with stand-ins like "My network" and "AP 1", so you can post screenshots without revealing where you are. Recordings keep the real values.
- The status bar now says when the network list comes from Windows' own scans rather than AirScope's.

### 1.1

- **No more scan lag by default.** In 1.0, AirScope scanned for neighbouring networks every 10 seconds whatever you were looking at. Each scan briefly takes the adapter off your channel, which showed up as regular signal dips, retry spikes and latency spikes. The new **Auto** mode only scans while Networks, Channels, History or Advisor is on screen. See [Scans and gaming](#scans-and-gaming).
- **Scan shading** on the live graph: blue for AirScope's own scans, amber for scans by Windows or other apps. Readings taken during scans are left out of the signal statistics and drop alerts.
- **Steadier retry rate**: a 10-second average with scans excluded, plus the recent peak, instead of a jumpy per-second figure.
- **Router ping** (optional): live latency and packet loss to your router, a latency strip under the graph, and events that say whether a spike had a scan behind it.
- **Cleaner spot tests**: AirScope never scans during a test, moments when Windows scanned are left out, and ping latency and loss are included when Router ping is on.
- **Honest freshness**: when AirScope isn't scanning, "last seen" times and History only move when something actually scans, rather than re-stamping old data.
- **Lighter in the background**: the live graph redraws less often when AirScope isn't the active window.

---

## Features

### Live

Your current connection, in real time.

- **Big signal readout** in dBm, colour-coded from Excellent to Unusable, with a quality bar.
- **Live signal graph** that scrolls continuously instead of jumping once per update. Choose a window of 30 seconds, 2, 5, 15 or 30 minutes.
- **Link rates**: transmit and receive speeds drawn on a second axis, so you can see how speed tracks signal.
- **Hover** anywhere on the graph to read the exact signal, rates and time at that moment. **Pause** freezes the graph so you can inspect it.
- **Long windows stay honest.** When zoomed out, a faint shaded band shows the minimum and maximum in each slice, so brief drops aren't averaged away.
- **Scan shading.** Moments when the adapter was off-channel scanning are shaded: blue for AirScope's own scans, amber for scans by Windows or other apps. Hovering over a point tells you if it was taken during a scan.
- **Clear** starts the graph and its statistics afresh, for example just before a speed test.
- **Signal statistics** over the chosen window: minimum, average, maximum and jitter (how much the signal wobbles), with a plain-English verdict. Readings taken during scans are left out, because they say nothing about your link.
- **Link details**: network, access point address (BSSID) and manufacturer, band and channel (with DFS flagged), channel width, Wi-Fi standard, security and encryption, protected management frames, the access point's theoretical top speed and antenna streams, retry rate (a 10-second average with scans excluded, plus the recent peak; marked "light traffic" when there's too little traffic to be reliable), client count and channel busyness (when the router advertises them), access point uptime, BSS colour, country code, and how long you've been connected.

### Router ping

Signal strength doesn't tell you what a dip actually *feels* like. Latency does. Tick **Router ping** above the live graph and AirScope pings your router twice a second.

- **Ping card** with current, average and 95th-percentile latency, packet loss, and a 60-second sparkline.
- **Latency strip** along the bottom of the live graph: green under 20 ms, amber when slower, red for spikes or lost pings.
- **Cause at a glance.** Every slow or lost ping is checked against scan periods, so the card tells you how many happened during scans and how many didn't. Spikes with no scan behind them are logged in Events.
- **Stays local.** It only pings your router (the Wi-Fi adapter's gateway, and only if it's on the Wi-Fi network itself), with packets that can't travel past it.
- Warns you if your router doesn't answer pings, or if another adapter such as Ethernet is on the same network and may be carrying the pings instead.

Off by default; your choice is remembered.

### Events

A running log of anything notable on your connection:

- **Roams**: when your PC moves from one access point to another (for example in a mesh system), with old and new channels.
- **Disconnects and reconnects**, including how long you were offline.
- **Brief drops** that recovered on their own.
- **Sudden signal drops** of 10 dB or more.
- **Link-rate collapses** that persist, as opposed to normal rate adjustment.
- **Router ping spikes** that no scan explains (when Router ping is on). These are rate-limited so they can't flood the log.
- **Channel changes** by your router. If it leaves a DFS channel, AirScope notes it may have been a **radar event**, which forces routers off those channels.

Short blips are filtered out, so a normal roam isn't logged as a disconnect followed by a reconnect. Signal-drop alerts ignore dips caused by scans.

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
- **Out-of-range networks** fade out instead of vanishing immediately. When AirScope isn't scanning, "out of range" is judged against the last scan, not the clock, so networks don't fade just because nothing has scanned for a while.
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

AirScope doesn't scan during a test, and moments when Windows scanned on its own are left out, so scans can't skew the result. **Scan now** is disabled while a test runs.

Each result records average, minimum and maximum signal, jitter, average transmit and receive rates, retry rate and roams. With Router ping on for the whole test, it also records average and 95th-percentile latency and packet loss. Results are saved between sessions. Mark one as the **baseline** (★) and every other run shows its difference in dB and transmit rate. Export everything to CSV.

Ideal for testing antenna upgrades, router placement, channel or band changes, and mesh node positions.

### Mini mode

A compact floating window showing live signal, quality, transmit/receive rates, network and channel (plus router ping, if on), with a 60-second sparkline. AirScope doesn't scan while Mini mode is open in Auto mode, so it's safe to leave over a game.

- Stays on top by default (toggle in Settings or by right-clicking it).
- Drag it anywhere; it remembers its position.
- Double-click it, or press ✕, to return to the full window.

Handy on a second monitor or while gaming.

### Recording

Press **Record** to log every live sample and every scan to CSV files in `Documents\AirScope\Logs`. With Router ping on, every ping (including lost ones) goes to a third file. The live-sample log has an `own_scan` column marking samples taken during AirScope's own scans, and a `tx_frames` column showing how many frames each retry figure is based on. The files open in Excel or any spreadsheet. Network names are escaped so they can't be interpreted as spreadsheet formulas.

---

## How it improves on the usual Windows Wi-Fi analyser

| | AirScope |
|---|---|
| **Responsiveness** | Your own signal is polled directly from the driver up to 10 times a second, separately from neighbour scanning, so it never waits on a scan. The graph scrolls smoothly in real time rather than stepping. |
| **Scan timing** | Waits for Windows to say a scan has finished, then reads the fresh results, rather than re-reading a stale cache on a timer. |
| **Gaming-friendly** | Only scans while you're looking at neighbouring networks, so it doesn't add lag spikes while you play. Scan periods are shaded on the graph and kept out of the statistics. |
| **Real latency** | Optional router ping shows latency and loss alongside signal, and says whether a spike had a scan behind it. |
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
| Neighbour scan interval | 3 – 60 s (default 10 s) | How often to scan when scanning is allowed. |
| When to scan | Auto (default), Always, Off | See [Scans and gaming](#scans-and-gaming). |
| Mini window stays on top | On / Off | |
| Screenshot mode | On / Off (default) | Hides network names and router addresses (BSSIDs) on screen with stand-ins like "My network" and "AP 1". Recordings keep the real values. |
| Router ping | On / Off (default) | The checkbox above the live graph. |

Settings are saved automatically.

---

## Scans and gaming

To see nearby networks, your Wi-Fi adapter has to scan: it briefly leaves your channel to listen on the others. While it's away you can get a short signal dip, a burst of retried frames and a latency spike, typically 50–200 ms. For browsing that's unnoticeable; for gaming or video calls it can be.

| Mode | What it does |
|---|---|
| **Auto** (default) | Scans only while Networks, Channels, History or Advisor is on screen. Stops on the Live tab, when minimised, in Mini mode, during spot tests, and a minute after you switch to another window (such as a game). The rest of the time it uses the results of Windows' own occasional background scans. |
| **Always** | Scans at your chosen interval, whatever is on screen. This is how 1.0 behaved. |
| **Off** | Never scans. Neighbour data comes only from Windows' background scans. **Scan now** still works when you want a fresh look. |

The status bar shows what scanning is doing right now, for example "auto: scans paused on this tab".

Windows itself also scans now and then: when you open the Wi-Fi menu, for location services, or when other apps ask. AirScope detects these when they finish and shades them in amber. Scans that the adapter's driver runs on its own while roaming aren't reported by Windows, so they can't be shown.

For the lowest possible latency while gaming, wired Ethernet avoids scan hiccups entirely.

---

## Files AirScope creates

| Location | Contents |
|---|---|
| `%AppData%\AirScope\settings.json` | Your settings. |
| `%AppData%\AirScope\spot-tests.json` | Saved spot-test results. If it ever becomes unreadable, a backup copy is kept next to it rather than being overwritten. |
| `%AppData%\AirScope\error.log` | Only created if something goes wrong. Useful for bug reports. |
| `Documents\AirScope\Logs\` | CSV recordings, only when you press Record: `link_*.csv` (live samples), `scan_*.csv` (networks seen) and, with Router ping on, `ping_*.csv`. |

To remove AirScope completely, delete the exe and these two folders.

---

## Accuracy and limitations

- **Signal readings come from your adapter's driver.** Different adapters and drivers can read several dB apart, so compare spot tests on the same PC and adapter.
- **Neighbour networks update at the scan interval**, typically every few seconds at best. That's a limit of Wi-Fi hardware and Windows, not AirScope. Only your own connection updates in real time. In Auto mode on the Live tab, or in Off mode, neighbour data (and link details that come from your router's beacons, such as clients, channel busyness and uptime) is only as fresh as Windows' last scan.
- **"AP max" is theoretical.** It's calculated from what the access point advertises (generation × channel width × streams). Your actual link speed is also limited by your own adapter, distance and interference, and real-world throughput is much lower than the link rate.
- **Noise floor and SNR aren't available.** Windows doesn't expose them to apps.
- **Clients and channel busyness** only appear when the router includes that information in its beacons. Many home routers don't.
- **Retry rate** depends on the driver reporting frame statistics. Some don't, in which case it shows "not reported by driver". On a quiet link it shows "light traffic" or "idle", because a few retried frames out of a few dozen look like a big percentage. For a meaningful figure, check it while the connection is busy (a download or speed test). Around 5–15% retries under load is normal on a healthy Wi-Fi 6 connection, and under 2% is excellent.
- **Not every scan can be shown.** Scans by Windows and other apps are only reported when they finish, so their start time is estimated. Roaming scans run by the driver itself aren't reported at all.
- **Router ping is a guide.** Routers answer pings at low priority, so an occasional spike can be the router being busy rather than your Wi-Fi.
- **The channel advisor is a guide, not a guarantee.** It only sees networks your PC can hear from where it sits, not what your router hears, and it can't detect non-Wi-Fi interference (microwaves, Bluetooth, baby monitors and so on).
- **Regulatory information reflects UK rules as of late 2026.** Rules change. Ofcom, for example, is consulting on opening more of the 6 GHz band. Always make sure your router's settings comply with the regulations where you are.
- **Vendor names** come from the public IEEE registry. Routers often use "virtual" addresses for guest or mesh networks; AirScope makes a best guess and labels these as virtual.

---

## Privacy and safety

- **Read-only.** AirScope never changes Windows settings, the registry, services, drivers, or your adapter or router configuration. The only thing it asks your adapter to do is scan for networks, which Windows does in the background anyway, and in Auto mode it only does that while you're looking at neighbouring networks.
- **No admin rights.** It runs with standard user permissions.
- **No internet access.** It makes no connections to the internet, has no telemetry, analytics or update checks, and has no accounts.
- **Router ping is the only traffic it sends**, apart from Wi-Fi scans, and it's off unless you tick it. It sends small ping packets to your router only: the Wi-Fi adapter's own gateway, and only if that's on the Wi-Fi network itself. The packets are marked so they can't travel past your router.
- **No third-party packages.** The manufacturer database is built in.
- **Your data stays on your PC.** Nothing is sent anywhere; router pings carry no data. Files are only written to the locations listed above.
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
| Few or no neighbour networks | Open the Networks or Channels tab (in Auto mode AirScope only scans while those are on screen), or press **Scan now**. Some adapters return fewer results while busy. |
| Retries look high when the PC is idle | That's normal: with little traffic, a few retries make a big percentage. AirScope marks this as "light traffic". Check retries during a download or speed test instead. |
| Sharing a screenshot | Turn on **Screenshot mode** in Settings first, so your network name and router address aren't visible. |
| Lag spikes while gaming | Use Auto (the default) or Off under **When to scan**, and keep AirScope on the Live tab or in Mini mode. Turn on Router ping to see whether spikes line up with scans. |
| Router ping says your router doesn't answer pings | Some routers ignore pings. AirScope slows to one ping every 5 seconds in case that changes; latency can't be measured this way on that router. |
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
