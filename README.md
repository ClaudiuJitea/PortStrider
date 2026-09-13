# PortStrider

Cross-platform Ethernet field tester for Linux and Windows. PortStrider brings LinkRunner-style diagnostics to a laptop: profile-driven AutoTest, switch discovery, packet capture, and local reporting — without a cloud dependency.

Built with **Avalonia 11** and **.NET 8**. Layer 2 work uses **SharpPcap** and **PacketDotNet**.

### What's new in v1.1

- **WiFi spectrum analyzer** — channel-overlap graphics for 2.4 / 5 / 6 GHz, per-BSSID RSSI history, band filters, single and live scans, and nearby access-point details (Linux `iw` + Windows WLAN API)
- **Cable diagnostics display** — visual link and port-media cards, four-pair TDR diagram with fault distances, and clear separation when TDR is not exposed by the driver
- **Linux privilege inheritance** — child processes (`iw`, `ip`, `ethtool`, etc.) inherit network capabilities; pre-flight distinguishes saved grants that need a restart

---

## Download

**[GitHub Releases](https://github.com/ClaudiuJitea/PortStrider/releases)** — latest: **v1.1.0**. Pick the asset for your platform:

| Platform | Release asset |
| --- | --- |
| Linux x64 | `PortStrider-linux-x64` |
| Linux arm64 | `PortStrider-linux-arm64` |
| Windows x64 | `PortStrider-win-x64.exe` |
| Windows arm64 | `PortStrider-win-arm64.exe` |

Verify checksums with `SHA256SUMS-supported` from the same release:

```bash
sha256sum -c SHA256SUMS-supported
```

Source-tree copies also live under [`publish/`](publish/) for cloning without using Releases.

On Linux, grant capture capabilities once after download (see [Linux capture privileges](#linux-capture-privileges)).

---

## Features

| Area | What it does |
| --- | --- |
| **AutoTest** | Profile-driven test sequence (LinkRunner G2 card order): link telemetry, 802.1X, DHCP DORA with vendor options, VLAN, gateway/target reachability, DNS, advertised PoE; stop-after-any-step; continuous re-probe; auto-run on link-up |
| **Connect settings** | Applies MAC, forced speed/duplex, 802.1Q VLAN, and static IPv4 for the test, then rolls back (Linux) |
| **802.1X** | `wpa_supplicant -D wired` — PEAP, TTLS, TLS, MD5; port stays authorized for the rest of the test |
| **Switch** | LLDP, CDP, and EDP decode; Voice VLAN from LLDP-MED / CDP; Flash Port blinks the switch LED by cycling the link |
| **Reflector** | Swaps MAC (and optionally IP + ports) and re-injects — peer throughput with another tester or `iperf3 -u` |
| **VLAN monitor** | Top nine VLANs by traffic share |
| **WiFi spectrum analyzer** | 2.4 / 5 / 6 GHz channel-overlap graphics, per-BSSID RSSI history, band filters, single/live scans, and nearby access-point details |
| **Capture** | Streaming PCAP with BPF filters, snap length, 2 GB cap |
| **Tools** | Ping, traceroute, TCP port probe, HTTP/TLS, iperf3 client/server |
| **Cable** | TDR via ethtool when the driver exposes it; SFP EEPROM/DDM when available |
| **Reports** | Local JSON, CSV, PDF, ZIP bundles, attachments, search, duplicate/delete |
| **Capabilities** | Honest feature matrix — hardware-dependent items marked unavailable instead of faked |

---

## Quick start

### Run from source

```bash
dotnet restore
dotnet run --project PortStrider.UI
```

### Linux capture privileges

Do not run the whole GUI as root. After building:

```bash
chmod +x scripts/grant-caps.sh
./scripts/grant-caps.sh
```

This applies `cap_net_raw`, `cap_net_admin`, and `cap_net_bind_service` (UDP/68 for DHCP) to the `PortStrider` binary. Restart after granting capabilities. PortStrider prepares the first two already-granted capabilities in the launching thread's inheritable and ambient sets so child processes (`iw`, `ip`, `ethtool`, `wpa_supplicant`, `dhclient`) inherit them. Network command helpers can fall back to passwordless `sudo -n` or `pkexec`; live WiFi scans report missing privileges without repeated password prompts. Pre-flight checks active network capabilities and inheritance, and distinguishes a saved grant that still needs a restart.

For a downloaded standalone binary, point the script at that path or run:

```bash
sudo setcap cap_net_raw,cap_net_admin,cap_net_bind_service+eip ./PortStrider
```

### Windows

1. Install [Npcap](https://npcap.com/#download) with WinPcap API-compatible mode.
2. Launch elevated if the NDIS filter refuses raw capture.

### External tools (optional)

Pre-flight checks list missing dependencies. Common ones:

- **Linux:** `ip`, `ethtool`, `wpa_supplicant`, `dhclient`, `iperf3`, `ping`, `traceroute`, `iw` (WiFi scans)
- **Windows:** `iperf3` (Npcap required for capture)

### WiFi spectrum analyzer

Select a WiFi adapter in the top toolbar and open **WiFi**. Choose a band and use **Scan once** or **Live scan**. The graph automatically fits the observed channel footprints. Scroll over it (or use +/−) to zoom, drag to pan, and use **Fit networks**, **Full band**, or **Focus selected** to adjust the view. Manual zoom is retained during live refresh. Click near a curve's signal level to inspect it; repeated clicks cycle coincident curves. You can also choose an SSID in the selector or access-point table. Colors match the curves, selector, and overlap list.

**Select strongest** chooses the highest RSSI in the current band. The details panel shows signal rank, BSSID, security, primary channel/frequency, width, estimated footprint, overlapping APs, same-primary-channel peers, last-seen time, and historical signal minimum/maximum/average. Ranking describes received signal only, not speed or Internet quality; overlap describes frequency footprints, not measured airtime. History follows the selected BSSID across the last 60 scans, including gaps when it disappears. Live scans wait five seconds after each completed sweep; Stop, changing adapters, and leaving the page cancel monitoring.

Pre-flight checks WiFi scan tools and adapter/radio availability. On Linux, repair **WiFi scan tools** to install `iw`; repair **Capture privileges** and restart the app to inherit CAP_NET_ADMIN for scanning. Enable WiFi and disable airplane mode. On Windows, scans use the native WLAN API and require WLAN AutoConfig, a working WiFi driver, and OS location access where required. Pre-flight reports failures with guidance; Npcap is not needed for this analyzer.

The graphs visualize access-point scan results, not raw RF spectrum: they cannot measure non-WiFi interference, noise floor, or airtime utilization. Available bands depend on the adapter, driver, and regulatory domain. Curves use advertised HT/VHT operating widths when parsed on Linux; unknown widths (including Windows and non-contiguous 80+80 MHz) use a labeled 20 MHz guide. Windows security is shown as Open/Protected. Failed scans retain the last completed result and timestamp.

### Cable diagnostics display

The Cable page summarizes physical link state, negotiated speed/duplex, local and partner advertised maximum speeds, auto-negotiation, MDI-X, and port media in visual cards. When a driver exposes TDR results, a four-pair cable diagram and colored pair cards show pass/open/short/check state and estimated fault distance. When the link works but the selected NIC does not expose TDR, the page shows a healthy link separately from an amber **TDR not exposed** result instead of presenting the driver error as a cable fault. Raw `ethtool` output remains available in a collapsed technical-details section.

Backend references: [Linux iw](https://wireless.docs.kernel.org/en/latest/en/users/documentation/iw.html), [Windows WLAN BSS API](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetnetworkbsslist).

---

## Build release binaries

Single-file, self-contained publish per RID:

```bash
RID=linux-x64   # or linux-arm64, win-x64, win-arm64, osx-x64, osx-arm64
dotnet publish PortStrider.UI -c Release -r $RID \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/${RID}-standalone
```

Regenerate checksums:

```bash
(cd publish && sha256sum *-standalone/PortStrider* > SHA256SUMS-supported)
```

---

## Solution layout

```
PortStrider.sln
├── PortStrider.Core              Models and service contracts
├── PortStrider.Infrastructure    Capture, probes, profiles, reports, platform
├── PortStrider.UI                Avalonia MVVM shell (assembly: PortStrider)
├── PortStrider.Core.Tests
└── PortStrider.Infrastructure.Tests
```

---

## Capability matrix

| Feature | Linux | Windows | Notes |
| --- | --- | --- | --- |
| AutoTest / targets | Yes | Yes | Uses host adapter routing |
| LLDP / CDP / EDP | Yes | Yes | Needs libpcap/Npcap + privileges |
| Active DHCP (DORA) | Yes | Degraded | Full DORA with Option 55; Linux binds to NIC (`SO_BINDTODEVICE`), Windows uses routing table |
| Advertised speed / MDI-X / downshift | Yes | Degraded | From `ethtool`; NDIS exposes actual speed only |
| Forced speed/duplex, user MAC, static IP | Yes | No | `ethtool -s`, `ip link`, `ip addr`; restored after test |
| VLAN tagging test | Yes | No | Temporary 802.1Q sub-interface |
| 802.1X test | Yes | No | `wpa_supplicant -D wired` |
| Voice VLAN | Yes | Yes | LLDP-MED Network Policy, CDP VoIP VLAN |
| Flash switch-port LED | Yes | No | Cycles link with `ip link` |
| Packet reflector | Yes | Yes | MAC or MAC+IP swap; needs capture privileges |
| Loaded PoE measurement | No | No | Advertised PoE from neighbor TLVs only |
| Cable TDR | Degraded | No | Driver-dependent ethtool |
| Wiremap / tone | No | No | Requires dedicated hardware |
| SFP diagnostics | Degraded | No | `ethtool -m` when exposed |
| Packet capture | Yes | Yes | Streams to PCAP |
| WiFi channel / signal analyzer | Yes | Yes | WiFi adapter required; Linux iw + CAP_NET_ADMIN; Windows native WLAN; scan data, not raw RF spectrum |
| iperf3 throughput | Yes | Yes | External binary |
| Local reports / bundles | Yes | Yes | No cloud dependency |

---

## Tests

```bash
dotnet test PortStrider.sln
```

---

## License

Source and binaries are provided as-is for field testing and evaluation. Add a `LICENSE` file if you intend to distribute under a specific open-source terms.
