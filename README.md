# PortStrider

Cross-platform Ethernet field tester for Linux and Windows. PortStrider brings LinkRunner-style diagnostics to a laptop: profile-driven AutoTest, switch discovery, packet capture, and local reporting — without a cloud dependency.

Built with **Avalonia 11** and **.NET 8**. Layer 2 work uses **SharpPcap** and **PacketDotNet**.

---

## Download

Pre-built single-file executables are in [`publish/`](publish/). Verify checksums before running:

```bash
cd publish && sha256sum -c SHA256SUMS-supported
```

| Platform | Path |
| --- | --- |
| Linux x64 | [`publish/linux-x64-standalone/PortStrider`](publish/linux-x64-standalone/PortStrider) |
| Linux arm64 | [`publish/linux-arm64-standalone/PortStrider`](publish/linux-arm64-standalone/PortStrider) |
| Windows x64 | [`publish/win-x64-standalone/PortStrider.exe`](publish/win-x64-standalone/PortStrider.exe) |
| Windows arm64 | [`publish/win-arm64-standalone/PortStrider.exe`](publish/win-arm64-standalone/PortStrider.exe) |

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

This applies `cap_net_raw`, `cap_net_admin`, and `cap_net_bind_service` (UDP/68 for DHCP) to the `PortStrider` binary. At startup PortStrider raises the first two capabilities into its ambient set so child processes (`ip`, `ethtool`, `wpa_supplicant`, `dhclient`) inherit them. If that fails, it falls back to passwordless `sudo -n` or `pkexec`.

For a downloaded standalone binary, point the script at that path or run:

```bash
sudo setcap cap_net_raw,cap_net_admin,cap_net_bind_service+eip ./PortStrider
```

### Windows

1. Install [Npcap](https://npcap.com/#download) with WinPcap API-compatible mode.
2. Launch elevated if the NDIS filter refuses raw capture.

### External tools (optional)

Pre-flight checks list missing dependencies. Common ones:

- **Linux:** `ip`, `ethtool`, `wpa_supplicant`, `dhclient`, `iperf3`, `ping`, `traceroute`
- **Windows:** `iperf3` (Npcap required for capture)

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
