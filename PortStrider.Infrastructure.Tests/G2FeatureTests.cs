using System.Net;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Infrastructure.Adapters;
using PortStrider.Infrastructure.Probes;
using PortStrider.Infrastructure.Sniffing;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class EthtoolSettingsTests
{
    private const string Sample = """
        Settings for eth0:
        	Supported ports: [ TP	 MII ]
        	Supported link modes:   10baseT/Half 10baseT/Full
        	                        100baseT/Half 100baseT/Full
        	                        1000baseT/Full
        	Supported pause frame use: Symmetric Receive-only
        	Supports auto-negotiation: Yes
        	Advertised link modes:  10baseT/Half 10baseT/Full
        	                        100baseT/Half 100baseT/Full
        	                        1000baseT/Full
        	Advertised auto-negotiation: Yes
        	Link partner advertised link modes:  10baseT/Half 10baseT/Full
        	                                     100baseT/Half 100baseT/Full
        	Link partner advertised auto-negotiation: Yes
        	Speed: 100Mb/s
        	Duplex: Full
        	Auto-negotiation: on
        	Port: Twisted Pair
        	PHYAD: 0
        	Transceiver: external
        	MDI-X: on (auto)
        	Link detected: yes
        """;

    [Fact]
    public void Parses_advertised_partner_speed_and_mdix()
    {
        var s = EthtoolSettings.Parse(Sample);
        Assert.Equal(1000, s.AdvertisedMaxMbps);
        Assert.Equal(100, s.PartnerMaxMbps);
        Assert.Equal(100, s.SpeedMbps);
        Assert.Equal("Full", s.Duplex);
        Assert.Equal("on (auto)", s.MdiX);
        Assert.Equal("Full/Half", s.AdvertisedDuplex);
        Assert.True(s.LinkDetected);
    }

    [Fact]
    public void No_downshift_when_partner_limits_speed()
    {
        // Local advertises 1G, partner only 100M, link is 100M → that's expected, not a downshift.
        var s = EthtoolSettings.Parse(Sample);
        Assert.False(s.IsDownshift);
    }

    [Fact]
    public void Downshift_when_both_advertise_more_than_negotiated()
    {
        var text = Sample.Replace("Link partner advertised link modes:  10baseT/Half 10baseT/Full\n\t                                     100baseT/Half 100baseT/Full",
            "Link partner advertised link modes:  1000baseT/Full");
        var s = EthtoolSettings.Parse(text);
        Assert.Equal(1000, s.PartnerMaxMbps);
        Assert.True(s.IsDownshift);
    }

    [Fact]
    public void Formats_speeds()
    {
        Assert.Equal("1 Gbps", EthtoolSettings.FormatMbps(1000));
        Assert.Equal("2.5 Gbps", EthtoolSettings.FormatMbps(2500));
        Assert.Equal("100 Mbps", EthtoolSettings.FormatMbps(100));
        Assert.Equal("—", EthtoolSettings.FormatMbps(0));
    }
}

public class DhcpPacketTests
{
    private static readonly byte[] Mac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    [Fact]
    public void Discover_sets_broadcast_flag_and_parameter_request_list()
    {
        var profile = TestProfile.Default() with { DhcpOption = DhcpOptionKind.Option150 };
        var pkt = DhcpPacket.Build(DhcpPacket.MessageType.Discover, Mac, 0x11223344, profile);

        Assert.Equal(1, pkt[0]);
        Assert.Equal(0x80, pkt[10]); // BROADCAST
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, pkt[4..8]);
        Assert.Equal(Mac, pkt[28..34]);

        var opts = ParseRaw(pkt);
        Assert.Equal(new byte[] { 1 }, opts[53]);
        Assert.Contains((byte)150, opts[55]);
        Assert.Contains((byte)51, opts[55]);
        Assert.Contains((byte)54, opts[55]);
        Assert.DoesNotContain(43, opts.Keys); // never send zero-byte 43/150
        Assert.DoesNotContain(150, opts.Keys);
        Assert.Equal(7, opts[61].Length);
    }

    [Fact]
    public void Request_carries_requested_ip_and_server_id()
    {
        var pkt = DhcpPacket.Build(DhcpPacket.MessageType.Request, Mac, 7, TestProfile.Default(), "10.0.0.50", "10.0.0.1");
        var opts = ParseRaw(pkt);
        Assert.Equal(new byte[] { 3 }, opts[53]);
        Assert.Equal(new byte[] { 10, 0, 0, 50 }, opts[50]);
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, opts[54]);
    }

    [Fact]
    public void Lease_time_is_big_endian_and_lists_decode()
    {
        byte[] options =
        [
            53, 1, 2,
            1, 4, 255, 255, 255, 0,
            3, 4, 10, 0, 0, 1,
            6, 8, 10, 0, 0, 1, 8, 8, 8, 8,
            51, 4, 0x00, 0x01, 0x51, 0x80, // 86400 s
            150, 8, 10, 1, 1, 1, 10, 1, 1, 2,
            43, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
            255
        ];
        var map = DhcpPacket.ParseOptions(options);
        Assert.Equal("86400", map[51]);
        Assert.Equal("255.255.255.0", map[1]);
        Assert.Equal("10.0.0.1, 8.8.8.8", map[6]);
        Assert.Equal("10.1.1.1, 10.1.1.2", map[150]);
        Assert.Equal("hello", map[43]);
        Assert.Equal("1d", DhcpTestService.FormatLease(86400));
        Assert.Equal("1h 30m", DhcpTestService.FormatLease(5400));
    }

    [Fact]
    public void Reply_parser_matches_xid_and_type()
    {
        var reply = new byte[300];
        reply[0] = 2;
        reply[4] = 0x00; reply[5] = 0x00; reply[6] = 0x00; reply[7] = 0x2A;
        reply[16] = 192; reply[17] = 168; reply[18] = 1; reply[19] = 77;
        reply[236] = 99; reply[237] = 130; reply[238] = 83; reply[239] = 99;
        reply[240] = 53; reply[241] = 1; reply[242] = 5;
        reply[243] = 54; reply[244] = 4; reply[245] = 192; reply[246] = 168; reply[247] = 1; reply[248] = 1;
        reply[249] = 255;

        var parsed = DhcpPacket.TryParseReply(reply, new IPEndPoint(IPAddress.Parse("192.168.1.1"), 67), 0x2A);
        Assert.NotNull(parsed);
        Assert.Equal(DhcpPacket.MessageType.Ack, parsed!.Type);
        Assert.Equal("192.168.1.77", parsed.YourIp);
        Assert.Equal("192.168.1.1", parsed.Options[54]);
        Assert.Null(DhcpPacket.TryParseReply(reply, parsed.Remote, 0x2B));
    }

    private static Dictionary<int, byte[]> ParseRaw(byte[] pkt)
    {
        var map = new Dictionary<int, byte[]>();
        var i = 240;
        while (i < pkt.Length)
        {
            var code = pkt[i++];
            if (code == 255) break;
            var len = pkt[i++];
            map[code] = pkt[i..(i + len)];
            i += len;
        }

        return map;
    }
}

public class VoiceVlanTests
{
    [Fact]
    public void Lldp_med_network_policy_sets_voice_vlan_not_port_vlan()
    {
        // TLV type 127, length 8: OUI 00-12-BB, subtype 2, app=1 (voice), T=1 VLAN=200, pri=5, DSCP=46
        // VLAN 200 = 0b00001_1001000 → 0x40|0x01 = 0x41, (0x48<<1)|(pri>>2 = 1) = 0x91, (pri&3)<<6 | 46 = 0x6E
        byte[] payload =
        [
            0xFE, 0x08, 0x00, 0x12, 0xBB, 0x02, 0x01, 0x41, 0x91, 0x6E,
            0xFE, 0x06, 0x00, 0x80, 0xC2, 0x01, 0x00, 0x0A, // 802.1 PVID = 10
            0x00, 0x00
        ];
        var b = new SwitchInfoBuilder();
        LldpParser.Parse(payload, b);
        var info = b.Build();
        Assert.Equal("200", info.VoiceVlan);
        Assert.Equal("10", info.VlanId);
        Assert.Contains(info.Details, d => d.Contains("MED Voice") && d.Contains("VLAN 200 tagged") && d.Contains("L2 pri 5") && d.Contains("DSCP 46"));
    }

    [Fact]
    public void Cdp_voip_vlan_reply_sets_voice_vlan()
    {
        var b = new SwitchInfoBuilder();
        // Ethernet + 802.2 LLC/SNAP (AA AA 03 + Cisco OUI + PID 0x2000) + CDP header (ver, ttl, checksum) + TLV 0x000E
        var frame = new byte[14 + 8 + 4 + 7];
        frame[0] = 0x01; frame[1] = 0x00; frame[2] = 0x0C; frame[3] = 0xCC; frame[4] = 0xCC; frame[5] = 0xCC;
        var snap = new byte[] { 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00 };
        Array.Copy(snap, 0, frame, 14, snap.Length);
        var tlv = new byte[] { 0x00, 0x0E, 0x00, 0x07, 0x00, 0x01, 0x2C };
        Array.Copy(tlv, 0, frame, 14 + 8 + 4, tlv.Length);
        CdpParser.Parse(frame.AsSpan(14), b);
        Assert.Equal("300", b.Build().VoiceVlan);
    }

    [Fact]
    public void Tagged_frames_are_counted_as_observed_vlans()
    {
        var b = new SwitchInfoBuilder();
        b.AddObservedVlan(20);
        b.AddObservedVlan(10);
        b.AddObservedVlan(20);
        Assert.Equal([10, 20], b.Build().ObservedVlans);

        var tagged = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0x81, 0x00, 0x20, 0x64, 0x08, 0x00, 0 };
        Assert.Equal(100, SwitchDiscoveryService.TagVid(tagged));
        Assert.Null(SwitchDiscoveryService.TagVid([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0x08, 0x00, 0, 0, 0, 0]));
    }
}

public class ReflectorFrameTests
{
    private static readonly byte[] Own = [0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
    private static readonly byte[] Peer = [0x02, 0x11, 0x22, 0x33, 0x44, 0x55];

    private static byte[] UdpFrame(byte[] dst, byte[] src, int dport)
    {
        var f = new byte[14 + 20 + 8 + 4];
        dst.CopyTo(f, 0); src.CopyTo(f, 6);
        f[12] = 0x08; f[13] = 0x00;
        f[14] = 0x45; f[23] = 17;
        f[26] = 10; f[27] = 0; f[28] = 0; f[29] = 1; // src 10.0.0.1
        f[30] = 10; f[31] = 0; f[32] = 0; f[33] = 2; // dst 10.0.0.2
        f[34] = 0xC3; f[35] = 0x50; // sport 50000
        f[36] = (byte)(dport >> 8); f[37] = (byte)dport;
        return f;
    }

    [Fact]
    public void Swaps_mac_ip_and_ports()
    {
        var frame = UdpFrame(Own, Peer, 5201);
        var ok = ReflectorFrame.TryBuild(frame, Own, new ReflectorOptions { Swap = ReflectorSwapMode.MacAndIp, Filter = ReflectorFilterMode.OwnMacAndNetAlly }, out var r);
        Assert.True(ok);
        Assert.Equal(Peer, r[0..6]);
        Assert.Equal(Own, r[6..12]);
        Assert.Equal(new byte[] { 10, 0, 0, 2 }, r[26..30]);
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, r[30..34]);
        Assert.Equal(5201, (r[34] << 8) | r[35]);
        Assert.Equal(50000, (r[36] << 8) | r[37]);
    }

    [Fact]
    public void Mac_only_leaves_ip_untouched()
    {
        var frame = UdpFrame(Own, Peer, 5201);
        Assert.True(ReflectorFrame.TryBuild(frame, Own, new ReflectorOptions { Swap = ReflectorSwapMode.MacOnly, Filter = ReflectorFilterMode.OwnMac }, out var r));
        Assert.Equal(Peer, r[0..6]);
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, r[26..30]);
    }

    [Fact]
    public void Never_reflects_own_or_control_or_service_traffic()
    {
        var opts = new ReflectorOptions { Filter = ReflectorFilterMode.OwnMacAndNetAlly };
        Assert.False(ReflectorFrame.TryBuild(UdpFrame(Peer, Own, 5201), Own, opts, out _)); // from us
        Assert.False(ReflectorFrame.TryBuild(UdpFrame(Peer, Peer, 5201), Own, opts, out _)); // not for us
        Assert.False(ReflectorFrame.TryBuild(UdpFrame(Own, Peer, 53), Own, opts, out _)); // DNS
        Assert.False(ReflectorFrame.TryBuild(UdpFrame(Own, Peer, 5353), Own, opts, out _)); // mDNS

        var arp = UdpFrame(Own, Peer, 5201);
        arp[12] = 0x08; arp[13] = 0x06;
        Assert.False(ReflectorFrame.TryBuild(arp, Own, new ReflectorOptions { Filter = ReflectorFilterMode.OwnMac }, out _));

        var eapol = UdpFrame(Own, Peer, 5201);
        eapol[12] = 0x88; eapol[13] = 0x8E;
        Assert.False(ReflectorFrame.TryBuild(eapol, Own, new ReflectorOptions { Filter = ReflectorFilterMode.All }, out _));
    }
}

public class DnsWireTests
{
    [Fact]
    public void Builds_and_parses_a_query_roundtrip()
    {
        var q = DnsWire.BuildAQuery(0x1234, "example.com");
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        Assert.Equal(7, q[12]); // "example"
        Assert.Equal(3, q[20]); // "com"

        // craft a response: copy question, set QR, ancount=1, then answer with pointer + A record
        var resp = new List<byte>(q);
        resp[2] = 0x81; resp[3] = 0x80;
        resp[7] = 1;
        resp.AddRange([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34]);
        Assert.True(DnsWire.TryParseResponse(resp.ToArray(), 0x1234, out var rcode, out var answer));
        Assert.Equal(0, rcode);
        Assert.Equal("93.184.216.34", answer);
        Assert.False(DnsWire.TryParseResponse(resp.ToArray(), 0x9999, out _, out _));
    }
}

public class InterfaceConfigHelperTests
{
    [Fact]
    public void Normalises_mac_and_cidr()
    {
        Assert.Equal("00:11:22:aa:bb:cc", InterfaceConfigService.NormalizeMac("00-11-22-AA-BB-CC"));
        Assert.Equal("00:11:22:aa:bb:cc", InterfaceConfigService.NormalizeMac("001122aabbcc"));
        Assert.Null(InterfaceConfigService.NormalizeMac("nope"));
        Assert.Equal("192.168.1.5/24", InterfaceConfigService.ToCidr("192.168.1.5", "255.255.255.0"));
        Assert.Equal("192.168.1.5/16", InterfaceConfigService.ToCidr("192.168.1.5", "/16"));
        Assert.Equal("10.0.0.9/30", InterfaceConfigService.ToCidr("10.0.0.9/30", null));
        Assert.Equal("10.0.0.9/24", InterfaceConfigService.ToCidr("10.0.0.9", null));
        Assert.Null(InterfaceConfigService.ToCidr("bad", null));
        Assert.Equal("enp0s31f6.100", InterfaceConfigService.VlanInterfaceName("enp0s31f6", 100));
        Assert.Equal("psv4094", InterfaceConfigService.VlanInterfaceName("averyveryverylongname", 4094));
    }

    [Fact]
    public void RequiresChanges_reflects_profile()
    {
        var svc = new InterfaceConfigService();
        Assert.False(svc.RequiresChanges(TestProfile.Default()));
        Assert.True(svc.RequiresChanges(TestProfile.Default() with { VlanId = 10 }));
        Assert.True(svc.RequiresChanges(TestProfile.Default() with { LinkSpeed = LinkSpeedSetting.Mbps100 }));
        Assert.True(svc.RequiresChanges(TestProfile.Default() with { OverrideMac = "00:11:22:33:44:55" }));
        Assert.True(svc.RequiresChanges(TestProfile.Default() with { AddressMode = AddressMode.Static, StaticIpv4 = "10.0.0.5" }));
        Assert.False(svc.RequiresChanges(TestProfile.Default() with { AddressMode = AddressMode.Static }));
    }

    [Fact]
    public void Wpa_config_reflects_eap_method()
    {
        var peap = WpaSupplicantDot1xService.BuildConfig(TestProfile.Default() with { Enable8021X = true, EapType = EapType.Peap, Username8021X = "bob", Password8021X = "p\"w" }, "/tmp/x");
        Assert.Contains("eap=PEAP", peap);
        Assert.Contains("identity=\"bob\"", peap);
        Assert.Contains("password=\"p\\\"w\"", peap);
        Assert.Contains("key_mgmt=IEEE8021X", peap);

        var tls = WpaSupplicantDot1xService.BuildConfig(TestProfile.Default() with { EapType = EapType.Tls, ClientCertPath8021X = "/c.pem", ClientKeyPath8021X = "/k.pem" }, "/tmp/x");
        Assert.Contains("eap=TLS", tls);
        Assert.Contains("client_cert=\"/c.pem\"", tls);
        Assert.DoesNotContain("password=", tls);
    }
}
