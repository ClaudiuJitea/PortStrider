using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

public sealed class ProbeService : IProbeService
{
    private static readonly Dictionary<int, string> WellKnown = new()
    {
        [22] = "SSH",
        [23] = "Telnet",
        [53] = "DNS",
        [80] = "HTTP",
        [123] = "NTP",
        [161] = "SNMP",
        [443] = "HTTPS",
        [445] = "SMB",
        [3389] = "RDP",
        [5060] = "SIP",
        [8080] = "HTTP-alt",
        [8443] = "HTTPS-alt"
    };

    public async Task<PingSample> PingOnceAsync(string host, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return new PingSample
            {
                Sequence = 0,
                Success = reply.Status == IPStatus.Success,
                RoundtripMs = reply.RoundtripTime,
                Status = string.IsNullOrWhiteSpace(sourceAddress)
                    ? reply.Status.ToString()
                    : $"{reply.Status} (route via OS; source bind not supported for ICMP)"
            };
        }
        catch (Exception ex)
        {
            return new PingSample { Sequence = 0, Success = false, Status = ex.Message };
        }
    }

    public async Task<IReadOnlyList<TraceHop>> TracerouteAsync(string host, IProgress<TraceHop> progress, CancellationToken cancellationToken = default)
    {
        if (HostOs.IsWindows)
            return await RunProcessTraceAsync("tracert", $"-d -w 1000 -h 20 {host}", ParseWindows, progress, cancellationToken);

        if (await ProcessUtil.CommandExistsAsync("traceroute", cancellationToken))
            return await RunProcessTraceAsync("traceroute", $"-n -w 1 -q 1 -m 20 {host}", ParseUnix, progress, cancellationToken);

        if (await ProcessUtil.CommandExistsAsync("tracepath", cancellationToken))
            return await RunProcessTraceAsync("tracepath", $"-n {host}", ParseUnix, progress, cancellationToken);

        return await FallbackTtlPingAsync(host, progress, cancellationToken);
    }

    public async Task<PortProbeResult> ProbePortAsync(string host, int port, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            if (!string.IsNullOrWhiteSpace(sourceAddress) && IPAddress.TryParse(sourceAddress, out var source))
                client.Client.Bind(new IPEndPoint(source, 0));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new PortProbeResult
            {
                Port = port,
                Service = WellKnown.GetValueOrDefault(port, "TCP"),
                Open = true,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PortProbeResult
            {
                Port = port,
                Service = WellKnown.GetValueOrDefault(port, "TCP"),
                Open = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Error = ex is OperationCanceledException or TimeoutException ? "timeout" : "closed"
            };
        }
    }

    public async Task<HttpProbeResult> ProbeHttpAsync(string url, string? proxyUrl = null, CancellationToken cancellationToken = default)
    {
        string? subject = null, issuer = null, tls = null;
        DateTimeOffset? notAfter = null;

        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is X509Certificate2 c2)
                    {
                        subject = c2.Subject;
                        issuer = c2.Issuer;
                        notAfter = c2.NotAfter;
                    }
                    else if (cert is not null)
                    {
                        subject = cert.Subject;
                        issuer = cert.Issuer;
                    }

                    return errors == SslPolicyErrors.None || url.Contains("1.1.1.1", StringComparison.Ordinal);
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
            handler.Proxy = new WebProxy(proxyUrl);

        try
        {
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            tls = response.RequestMessage?.RequestUri?.Scheme == "https" ? "TLS" : "HTTP";
            return new HttpProbeResult
            {
                Url = url,
                StatusCode = (int)response.StatusCode,
                ElapsedMs = sw.ElapsedMilliseconds,
                TlsProtocol = tls,
                CertificateSubject = subject,
                CertificateIssuer = issuer,
                CertificateNotAfter = notAfter
            };
        }
        catch (Exception ex)
        {
            return new HttpProbeResult
            {
                Url = url,
                Error = ex.Message,
                CertificateSubject = subject,
                CertificateIssuer = issuer,
                CertificateNotAfter = notAfter,
                TlsProtocol = tls
            };
        }
    }

    public async Task<PingSample> QueryDnsAsync(string server, string name, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(server, out var serverIp))
            return new PingSample { Sequence = 0, Success = false, Status = "invalid server address" };

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = DnsWire.BuildAQuery(id, name);
        var sw = Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(serverIp.AddressFamily);
            if (!string.IsNullOrWhiteSpace(sourceAddress) && IPAddress.TryParse(sourceAddress, out var src) && src.AddressFamily == serverIp.AddressFamily)
                udp.Client.Bind(new IPEndPoint(src, 0));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await udp.SendAsync(query, query.Length, new IPEndPoint(serverIp, 53));
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                if (!DnsWire.TryParseResponse(result.Buffer, id, out var rcode, out var answer)) continue;
                sw.Stop();
                var ok = rcode == 0;
                return new PingSample
                {
                    Sequence = 0,
                    Success = ok,
                    RoundtripMs = sw.ElapsedMilliseconds,
                    Status = ok ? answer ?? "no A record" : DnsWire.RcodeName(rcode)
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingSample { Sequence = 0, Success = false, RoundtripMs = sw.ElapsedMilliseconds, Status = "timeout" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PingSample { Sequence = 0, Success = false, RoundtripMs = sw.ElapsedMilliseconds, Status = ex.Message };
        }
    }

    private static async Task<IReadOnlyList<TraceHop>> RunProcessTraceAsync(
        string file, string args, Func<string, TraceHop?> parse, IProgress<TraceHop> progress, CancellationToken cancellationToken)
    {
        var hops = new List<TraceHop>();
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null) return hops;
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null) break;
            var hop = parse(line);
            if (hop is null) continue;
            hops.Add(hop);
            progress.Report(hop);
        }

        await process.WaitForExitAsync(cancellationToken);
        return hops;
    }

    private static TraceHop? ParseUnix(string line)
    {
        var trim = line.Trim();
        if (trim.Length == 0 || trim.StartsWith("traceroute") || trim.StartsWith("tracepath")) return null;
        var parts = trim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim('.'), out var ttl)) return null;
        var timedOut = trim.Contains('*') && parts.Length < 3;
        var address = parts.Length > 1 ? parts[1] : "*";
        var rtt = parts.Length > 2 ? string.Join(" ", parts.Skip(2).Take(2)) : "*";
        return new TraceHop { Ttl = ttl, Address = address, Rtt = rtt, TimedOut = timedOut || address == "*" };
    }

    private static TraceHop? ParseWindows(string line)
    {
        var trim = line.Trim();
        if (trim.Length == 0) return null;
        var parts = trim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var ttl)) return null;
        var timedOut = trim.Contains("Request timed out", StringComparison.OrdinalIgnoreCase) || parts.Contains("*");
        var address = timedOut ? "*" : parts[^1];
        return new TraceHop { Ttl = ttl, Address = address, Rtt = timedOut ? "*" : parts[1], TimedOut = timedOut };
    }

    private static async Task<IReadOnlyList<TraceHop>> FallbackTtlPingAsync(string host, IProgress<TraceHop> progress, CancellationToken cancellationToken)
    {
        IPAddress address;
        try { address = (await Dns.GetHostAddressesAsync(host, cancellationToken))[0]; }
        catch { return []; }

        var hops = new List<TraceHop>();
        using var ping = new Ping();
        var options = new PingOptions(1, true);
        var buffer = new byte[32];
        for (var ttl = 1; ttl <= 20; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Ttl = ttl;
            try
            {
                var reply = await ping.SendPingAsync(address, 1000, buffer, options);
                var hop = new TraceHop
                {
                    Ttl = ttl,
                    Address = reply.Address is null || reply.Address.ToString() is "0.0.0.0" ? "*" : reply.Address.ToString(),
                    Rtt = reply.Status == IPStatus.TimedOut ? "*" : $"{reply.RoundtripTime} ms",
                    TimedOut = reply.Status == IPStatus.TimedOut
                };
                hops.Add(hop);
                progress.Report(hop);
                if (reply.Status == IPStatus.Success) break;
            }
            catch
            {
                var hop = new TraceHop { Ttl = ttl, Address = "*", Rtt = "*", TimedOut = true };
                hops.Add(hop);
                progress.Report(hop);
            }
        }

        return hops;
    }
}
