using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

/// <summary>
/// Active DHCP test that performs the full DORA exchange (Discover → Offer → Request → Ack) with the adapter's own MAC,
/// mirroring the LinkRunner G2 DHCP card: Discover status, Offer time, Request status, ACK time, server, subnet,
/// Option 43/150, lease time. Because the same MAC/client-id is used, the server hands back the lease the OS already
/// holds instead of consuming a second address.
/// </summary>
public sealed class DhcpTestService : IDhcpTestService
{
    private const int OfferTimeoutMs = 8000;
    private const int AckTimeoutMs = 5000;
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    public async Task<DhcpTestResult> DiscoverAsync(AdapterInfo adapter, TestProfile profile, bool allowRenew, CancellationToken cancellationToken = default)
    {
        if (allowRenew && HostOs.IsLinux && profile.AddressMode == AddressMode.Dhcp)
            return await LinuxRenewAsync(adapter, profile, cancellationToken);

        return await RawDoraAsync(adapter, profile, cancellationToken);
    }

    private static async Task<DhcpTestResult> RawDoraAsync(AdapterInfo adapter, TestProfile profile, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        UdpClient client;
        try
        {
            client = OpenClient(adapter);
        }
        catch (Exception ex)
        {
            return Unsupported($"Cannot open UDP/68 on {adapter.Name}: {Short(ex.Message)}");
        }

        using (client)
        {
            var mac = DhcpPacket.ParseMac(adapter.MacAddress);
            if (mac.Length != 6) return Unsupported("Adapter MAC address unavailable.");

            var xid = Random.Shared.Next();
            var discover = DhcpPacket.Build(DhcpPacket.MessageType.Discover, mac, xid, profile);
            long discoverMs;
            try
            {
                await client.SendAsync(discover, discover.Length, new IPEndPoint(IPAddress.Broadcast, 67));
                discoverMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                return Unsupported($"Discover could not be sent: {Short(ex.Message)}");
            }

            var offer = await WaitForAsync(client, xid, DhcpPacket.MessageType.Offer, OfferTimeoutMs, cancellationToken);
            if (offer is null)
            {
                return new DhcpTestResult
                {
                    Supported = true,
                    Status = TestStatus.Fail,
                    Summary = $"Discover sent, no Offer within {OfferTimeoutMs / 1000}s",
                    DiscoverMs = discoverMs,
                    InterfaceName = adapter.Name
                };
            }

            var offerMs = sw.ElapsedMilliseconds - discoverMs;
            var serverIp = offer.Options.GetValueOrDefault(54) ?? offer.Remote.Address.ToString();

            // Request the offered address from the offering server, then time the ACK.
            var requestStart = sw.ElapsedMilliseconds;
            var request = DhcpPacket.Build(DhcpPacket.MessageType.Request, mac, xid, profile, offer.YourIp, serverIp);
            var requestSent = false;
            DhcpPacket.Reply? ack = null;
            try
            {
                await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, 67));
                requestSent = true;
                ack = await WaitForAsync(client, xid, DhcpPacket.MessageType.Ack, AckTimeoutMs, cancellationToken, DhcpPacket.MessageType.Nak);
            }
            catch
            {
                // Request failure is reported below; the offer still counts.
            }

            var ackMs = ack is null ? 0 : sw.ElapsedMilliseconds - requestStart;
            var final = ack is { Type: DhcpPacket.MessageType.Ack } ? ack : offer;
            var opts = final.Options;
            var leaseSeconds = opts.TryGetValue(51, out var lease) && long.TryParse(lease, out var secs) ? secs : 0;

            var status = ack switch
            {
                { Type: DhcpPacket.MessageType.Ack } => TestStatus.Pass,
                { Type: DhcpPacket.MessageType.Nak } => TestStatus.Fail,
                _ => TestStatus.Warning
            };
            var summary = ack switch
            {
                { Type: DhcpPacket.MessageType.Ack } => $"Offer {offer.YourIp} in {offerMs} ms · Ack in {ackMs} ms from {serverIp}",
                { Type: DhcpPacket.MessageType.Nak } => $"Offer {offer.YourIp} in {offerMs} ms · server {serverIp} NAKed the request",
                _ => requestSent
                    ? $"Offer {offer.YourIp} in {offerMs} ms · no Ack within {AckTimeoutMs / 1000}s"
                    : $"Offer {offer.YourIp} in {offerMs} ms · Request could not be sent"
            };

            return new DhcpTestResult
            {
                Supported = true,
                Status = status,
                Summary = summary,
                DiscoverMs = discoverMs,
                OfferMs = offerMs,
                RequestMs = requestSent ? requestStart - discoverMs : 0,
                AckMs = ackMs,
                RequestSent = requestSent,
                AckReceived = ack is { Type: DhcpPacket.MessageType.Ack },
                ServerIp = serverIp,
                OfferedIp = final.YourIp,
                Subnet = opts.GetValueOrDefault(1),
                Gateway = opts.GetValueOrDefault(3),
                Dns = opts.GetValueOrDefault(6),
                DomainName = opts.GetValueOrDefault(15),
                Option43 = opts.GetValueOrDefault(43),
                Option60 = opts.GetValueOrDefault(60),
                Option150 = opts.GetValueOrDefault(150),
                LeaseTime = leaseSeconds > 0 ? FormatLease(leaseSeconds) : null,
                InterfaceName = adapter.Name
            };
        }
    }

    private static UdpClient OpenClient(AdapterInfo adapter)
    {
        var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.ExclusiveAddressUse = false;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (HostOs.IsLinux)
        {
            // Pin the socket to the NIC under test so the broadcast leaves (and replies arrive) on that port only.
            try
            {
                client.Client.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(adapter.Name + "\0"));
            }
            catch
            {
                // Needs CAP_NET_RAW; fall back to routing-table behaviour.
            }
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 68));
        }
        else if (adapter.Ipv4 is not null && IPAddress.TryParse(adapter.Ipv4, out var local))
        {
            // Windows delivers subnet/limited broadcasts to sockets bound to the interface address, and routes sends out of it.
            try { client.Client.Bind(new IPEndPoint(local, 68)); }
            catch { client.Client.Bind(new IPEndPoint(IPAddress.Any, 68)); }
        }
        else
        {
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 68));
        }

        return client;
    }

    private static async Task<DhcpPacket.Reply?> WaitForAsync(
        UdpClient client, int xid, DhcpPacket.MessageType wanted, int timeoutMs, CancellationToken cancellationToken, DhcpPacket.MessageType? alsoAccept = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cts.Token);
                var reply = DhcpPacket.TryParseReply(result.Buffer, result.RemoteEndPoint, xid);
                if (reply is null) continue;
                if (reply.Type == wanted || (alsoAccept is not null && reply.Type == alsoAccept)) return reply;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task<DhcpTestResult> LinuxRenewAsync(AdapterInfo adapter, TestProfile profile, CancellationToken cancellationToken)
    {
        if (!await ProcessUtil.CommandExistsAsync("dhclient", cancellationToken))
            return await RawDoraAsync(adapter, profile, cancellationToken);

        var sw = Stopwatch.StartNew();
        var output = await PrivilegedProcess.RunAsync("dhclient", ["-v", "-1", adapter.Name], cancellationToken, TimeSpan.FromSeconds(45));
        if (!output.Success)
            return Unsupported("dhclient renewal failed.");

        var props = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Name == adapter.Name)?.GetIPProperties();
        var ipv4 = props?.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        var gateway = props?.GatewayAddresses.FirstOrDefault()?.Address.ToString();
        var dns = string.Join(", ", props?.DnsAddresses.Select(a => a.ToString()) ?? []);
        var ackMs = sw.ElapsedMilliseconds;

        return new DhcpTestResult
        {
            Supported = true,
            Status = string.IsNullOrWhiteSpace(ipv4) ? TestStatus.Warning : TestStatus.Pass,
            Summary = string.IsNullOrWhiteSpace(ipv4) ? "Renew completed but no IPv4 lease observed." : $"Ack {ipv4} in {ackMs} ms",
            AckMs = ackMs,
            RequestSent = true,
            AckReceived = !string.IsNullOrWhiteSpace(ipv4),
            OfferedIp = ipv4,
            Gateway = gateway,
            Dns = dns,
            ServerIp = adapter.DhcpServers.FirstOrDefault(),
            InterfaceName = adapter.Name
        };
    }

    private static DhcpTestResult Unsupported(string summary) => new()
    {
        Supported = false,
        Status = TestStatus.Skipped,
        Summary = summary
    };

    internal static string FormatLease(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return ts.Hours > 0 ? $"{(int)ts.TotalDays}d {ts.Hours}h" : $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1) return ts.Minutes > 0 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{seconds}s";
    }

    private static string Short(string message) => message.Length <= 120 ? message : message[..117] + "…";
}

/// <summary>DHCPv4 wire encoding/decoding (RFC 2131/2132) for the active test.</summary>
internal static class DhcpPacket
{
    public enum MessageType : byte
    {
        Discover = 1,
        Offer = 2,
        Request = 3,
        Decline = 4,
        Ack = 5,
        Nak = 6,
        Release = 7,
        Inform = 8
    }

    public sealed class Reply
    {
        public required MessageType Type { get; init; }
        public required string YourIp { get; init; }
        public required IPEndPoint Remote { get; init; }
        public required IReadOnlyDictionary<int, string> Options { get; init; }
    }

    private static readonly byte[] BaseParameterRequest = [1, 3, 6, 12, 15, 28, 42, 51, 54, 58, 59];

    public static byte[] Build(MessageType type, byte[] mac, int xid, TestProfile profile, string? requestedIp = null, string? serverId = null)
    {
        var packet = new byte[576];
        packet[0] = 1; // BOOTREQUEST
        packet[1] = 1; // Ethernet
        packet[2] = 6; // hlen
        packet[4] = (byte)(xid >> 24);
        packet[5] = (byte)(xid >> 16);
        packet[6] = (byte)(xid >> 8);
        packet[7] = (byte)xid;
        packet[10] = 0x80; // BROADCAST flag: we have no address yet, so replies must be broadcast
        mac.CopyTo(packet, 28);
        packet[236] = 99; packet[237] = 130; packet[238] = 83; packet[239] = 99;

        var opt = 240;
        packet[opt++] = 53; packet[opt++] = 1; packet[opt++] = (byte)type;

        // Client identifier: hardware type + MAC, so the server matches the OS lease for this NIC.
        packet[opt++] = 61; packet[opt++] = 7; packet[opt++] = 1;
        mac.CopyTo(packet, opt); opt += 6;

        var host = Encoding.ASCII.GetBytes("PortStrider");
        packet[opt++] = 12; packet[opt++] = (byte)host.Length;
        host.CopyTo(packet, opt); opt += host.Length;

        if (requestedIp is not null && IPAddress.TryParse(requestedIp, out var req))
        {
            packet[opt++] = 50; packet[opt++] = 4;
            req.GetAddressBytes().CopyTo(packet, opt); opt += 4;
        }

        if (serverId is not null && IPAddress.TryParse(serverId, out var srv))
        {
            packet[opt++] = 54; packet[opt++] = 4;
            srv.GetAddressBytes().CopyTo(packet, opt); opt += 4;
        }

        if (profile.DhcpOption == DhcpOptionKind.Option60 && !string.IsNullOrWhiteSpace(profile.DhcpVendorClass))
        {
            var vendor = Encoding.ASCII.GetBytes(profile.DhcpVendorClass);
            var len = Math.Min(vendor.Length, 255);
            packet[opt++] = 60; packet[opt++] = (byte)len;
            Array.Copy(vendor, 0, packet, opt, len); opt += len;
        }

        // Parameter Request List — this is what makes the server return 43/150 (and subnet/router/DNS/lease).
        var requested = new List<byte>(BaseParameterRequest);
        if (profile.DhcpOption is DhcpOptionKind.Option43 or DhcpOptionKind.Option60 && !requested.Contains(43)) requested.Add(43);
        if (profile.DhcpOption is DhcpOptionKind.Option150 && !requested.Contains(150)) requested.Add(150);
        if (profile.DhcpOption == DhcpOptionKind.Option60 && !requested.Contains(60)) requested.Add(60);
        packet[opt++] = 55; packet[opt++] = (byte)requested.Count;
        requested.CopyTo(packet, opt); opt += requested.Count;

        packet[opt++] = 255;
        var min = 300; // keep well above the 240-byte minimum so picky servers/relays accept it
        return packet[..Math.Max(opt, min)];
    }

    public static Reply? TryParseReply(byte[] buffer, IPEndPoint remote, int xid)
    {
        if (buffer.Length < 240) return null;
        if (buffer[0] != 2) return null; // BOOTREPLY
        var replyXid = (buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7];
        if (replyXid != xid) return null;
        if (buffer[236] != 99 || buffer[237] != 130 || buffer[238] != 83 || buffer[239] != 99) return null;

        var options = ParseOptions(buffer.AsSpan(240));
        if (!options.TryGetValue(53, out var typeText) || !byte.TryParse(typeText, out var typeByte)) return null;

        return new Reply
        {
            Type = (MessageType)typeByte,
            YourIp = new IPAddress(buffer.AsSpan(16, 4)).ToString(),
            Remote = remote,
            Options = options
        };
    }

    internal static Dictionary<int, string> ParseOptions(ReadOnlySpan<byte> options)
    {
        var map = new Dictionary<int, string>();
        var i = 0;
        while (i < options.Length)
        {
            var code = options[i++];
            if (code == 255) break;
            if (code == 0) continue;
            if (i >= options.Length) break;
            var len = options[i++];
            if (i + len > options.Length) break;
            var value = options.Slice(i, len);
            i += len;
            map[code] = Decode(code, value);
        }

        return map;
    }

    private static string Decode(int code, ReadOnlySpan<byte> value) => code switch
    {
        1 or 28 or 50 or 54 when value.Length >= 4 => Ip(value[..4]),
        3 or 6 or 42 or 150 => string.Join(", ", Chunk(value, 4).Select(Ip)),
        51 or 58 or 59 when value.Length >= 4 => ReadUInt32BigEndian(value).ToString(),
        53 when value.Length >= 1 => value[0].ToString(),
        12 or 15 or 60 => Encoding.ASCII.GetString(value).Trim('\0'),
        43 => DecodeVendorSpecific(value),
        _ => Convert.ToHexString(value)
    };

    private static string DecodeVendorSpecific(ReadOnlySpan<byte> value)
    {
        // Option 43 is opaque. If it is printable text (Aruba/Ruckus/Cisco often send controller IPs as ASCII) show it as
        // such; if it is exactly one or more IPv4 addresses show dotted quads; else hex.
        var printable = value.Length > 0 && value.ToArray().All(b => b is >= 0x20 and < 0x7F);
        if (printable) return Encoding.ASCII.GetString(value);
        if (value.Length > 0 && value.Length % 4 == 0 && value.Length <= 16) return string.Join(", ", Chunk(value, 4).Select(Ip));
        return Convert.ToHexString(value);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> v) =>
        ((uint)v[0] << 24) | ((uint)v[1] << 16) | ((uint)v[2] << 8) | v[3];

    private static string Ip(byte[] bytes) => new IPAddress(bytes).ToString();
    private static string Ip(ReadOnlySpan<byte> bytes) => new IPAddress(bytes[..4].ToArray()).ToString();

    private static List<byte[]> Chunk(ReadOnlySpan<byte> data, int size)
    {
        var list = new List<byte[]>();
        for (var i = 0; i + size <= data.Length; i += size)
            list.Add(data.Slice(i, size).ToArray());
        return list;
    }

    public static byte[] ParseMac(string mac)
    {
        try
        {
            var parts = mac.Split([':', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && parts[0].Length == 12)
                return Convert.FromHexString(parts[0]);
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }
        catch
        {
            return [];
        }
    }
}
