using System.Diagnostics;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PortStrider.Infrastructure.Sniffing;

/// <summary>
/// LinkRunner G2 style packet reflector: frames addressed to this NIC are sent straight back with source/destination
/// MAC (and optionally IP + L4 ports) swapped so a peer tester (iperf3 UDP, NetAlly/Fluke performance tests, or the
/// Tools page of another PortStrider) can measure round-trip throughput and loss.
/// </summary>
public sealed class ReflectorService : IReflectorService, IDisposable
{
    private ILiveDevice? _device;
    private IDisposable? _lease;
    private PacketArrivalEventHandler? _handler;
    private long _framesIn, _framesOut, _bytesIn, _bytesOut, _filtered;
    private DateTimeOffset _startedAt;
    private long _lastRaise;
    private ReflectorOptions _options = new();
    private byte[] _ownMac = new byte[6];

    public bool IsRunning { get; private set; }
    public event EventHandler<ReflectorStatistics>? Updated;

    public ReflectorStatistics Statistics => new()
    {
        FramesReceived = Interlocked.Read(ref _framesIn),
        FramesReflected = Interlocked.Read(ref _framesOut),
        BytesReceived = Interlocked.Read(ref _bytesIn),
        BytesReflected = Interlocked.Read(ref _bytesOut),
        FramesFiltered = Interlocked.Read(ref _filtered),
        Duration = IsRunning ? DateTimeOffset.Now - _startedAt : TimeSpan.Zero
    };

    public Task StartAsync(string captureDevice, ReflectorOptions options, CancellationToken cancellationToken = default)
    {
        StopInternal();
        _ownMac = ReflectorFrame.ParseMac(options.OwnMac);
        if (_ownMac.Length != 6) throw new ArgumentException("Reflector needs the adapter MAC address.");
        _options = options;
        _framesIn = _framesOut = _bytesIn = _bytesOut = _filtered = 0;
        _startedAt = DateTimeOffset.Now;

        var device = FindDevice(captureDevice) ?? throw new InvalidOperationException($"Capture device '{captureDevice}' not found.");
        _lease = CaptureDeviceLease.Acquire(device.Name);
        try
        {
            device.Open(new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 250, Snaplen = 65535 });
            try
            {
                var mac = ReflectorFrame.FormatMac(_ownMac);
                device.Filter = options.Filter == ReflectorFilterMode.All
                    ? $"not ether src {mac}"
                    : $"ether dst {mac} and not ether src {mac}";
            }
            catch
            {
                // software filtering below still applies
            }

            _handler = (_, e) =>
            {
                try
                {
                    var raw = e.GetPacket().Data;
                    Interlocked.Increment(ref _framesIn);
                    Interlocked.Add(ref _bytesIn, raw.Length);
                    if (!ReflectorFrame.TryBuild(raw, _ownMac, _options, out var reflected))
                    {
                        Interlocked.Increment(ref _filtered);
                    }
                    else
                    {
                        _device?.SendPacket(reflected);
                        Interlocked.Increment(ref _framesOut);
                        Interlocked.Add(ref _bytesOut, reflected.Length);
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (now - Interlocked.Read(ref _lastRaise) > Stopwatch.Frequency / 4)
                    {
                        Interlocked.Exchange(ref _lastRaise, now);
                        Updated?.Invoke(this, Statistics);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _filtered);
                }
            };
            device.OnPacketArrival += _handler;
            device.StartCapture();
            _device = device;
            IsRunning = true;
            return Task.CompletedTask;
        }
        catch
        {
            try { device.Close(); } catch { /* ignore */ }
            _handler = null;
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public Task StopAsync()
    {
        StopInternal();
        Updated?.Invoke(this, Statistics);
        return Task.CompletedTask;
    }

    private static ILiveDevice? FindDevice(string name)
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            if (device.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (device.Description?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
                return device;
            if (device is LibPcapLiveDevice live && (live.Interface.FriendlyName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
                return device;
        }

        return null;
    }

    private void StopInternal()
    {
        IsRunning = false;
        if (_device is not null)
        {
            try
            {
                if (_handler is not null) _device.OnPacketArrival -= _handler;
                _device.StopCapture();
                _device.Close();
            }
            catch
            {
                // already stopped
            }
        }

        _device = null;
        _handler = null;
        _lease?.Dispose();
        _lease = null;
    }

    public void Dispose() => StopInternal();
}

/// <summary>Pure frame rewriting for the reflector. Kept free of pcap so it can be unit-tested.</summary>
internal static class ReflectorFrame
{
    private static readonly HashSet<int> NeverReflectUdpPorts = [53, 67, 68, 123, 137, 138, 1900, 5353, 5355];

    public static bool TryBuild(byte[] frame, byte[] ownMac, ReflectorOptions options, out byte[] reflected)
    {
        reflected = [];
        if (frame.Length < 14) return false;

        var dst = frame.AsSpan(0, 6);
        var src = frame.AsSpan(6, 6);
        if (src.SequenceEqual(ownMac)) return false; // never reflect our own frames (prevents loops)
        if (options.Filter != ReflectorFilterMode.All && !dst.SequenceEqual(ownMac)) return false;
        if (IsLinkControl(dst)) return false;

        var etherType = (frame[12] << 8) | frame[13];
        var l3 = 14;
        if (etherType == 0x8100 && frame.Length >= 18)
        {
            etherType = (frame[16] << 8) | frame[17];
            l3 = 18;
        }

        // Control protocols that must never be answered by a reflector.
        if (etherType is 0x0806 or 0x888E or 0x88CC or 0x8809) return false;

        if (options.Filter == ReflectorFilterMode.OwnMacAndNetAlly && !LooksLikeTestTraffic(frame, etherType, l3))
            return false;

        var copy = (byte[])frame.Clone();
        Swap(copy, 0, 6, 6);

        if (options.Swap == ReflectorSwapMode.MacAndIp)
        {
            if (etherType == 0x0800 && copy.Length >= l3 + 20)
            {
                var ihl = (copy[l3] & 0x0F) * 4;
                var proto = copy[l3 + 9];
                Swap(copy, l3 + 12, l3 + 16, 4); // src/dst IPv4 — header checksum is order-independent, no recompute needed
                var l4 = l3 + ihl;
                if (proto is 6 or 17 && copy.Length >= l4 + 4)
                    Swap(copy, l4, l4 + 2, 2); // ports — pseudo-header + ports swap symmetrically, checksums stay valid
            }
            else if (etherType == 0x86DD && copy.Length >= l3 + 40)
            {
                var next = copy[l3 + 6];
                Swap(copy, l3 + 8, l3 + 24, 16);
                var l4 = l3 + 40;
                if (next is 6 or 17 && copy.Length >= l4 + 4)
                    Swap(copy, l4, l4 + 2, 2);
            }
        }

        reflected = copy;
        return true;
    }

    private static bool LooksLikeTestTraffic(byte[] frame, int etherType, int l3)
    {
        if (etherType == 0x0800 && frame.Length >= l3 + 20)
        {
            var ihl = (frame[l3] & 0x0F) * 4;
            var proto = frame[l3 + 9];
            if (proto != 17) return false;
            var l4 = l3 + ihl;
            if (frame.Length < l4 + 8) return false;
            var dport = (frame[l4 + 2] << 8) | frame[l4 + 3];
            if (dport < 1024 || NeverReflectUdpPorts.Contains(dport)) return false;
            return true;
        }

        if (etherType == 0x86DD && frame.Length >= l3 + 48)
        {
            if (frame[l3 + 6] != 17) return false;
            var l4 = l3 + 40;
            var dport = (frame[l4 + 2] << 8) | frame[l4 + 3];
            return dport >= 1024 && !NeverReflectUdpPorts.Contains(dport);
        }

        return false;
    }

    private static bool IsLinkControl(ReadOnlySpan<byte> dst) =>
        // 01:80:C2:00:00:0x — STP, LLDP, EAPOL, pause frames
        dst[0] == 0x01 && dst[1] == 0x80 && dst[2] == 0xC2 && dst[3] == 0x00 && dst[4] == 0x00;

    private static void Swap(byte[] buffer, int a, int b, int length)
    {
        Span<byte> tmp = stackalloc byte[16];
        buffer.AsSpan(a, length).CopyTo(tmp);
        buffer.AsSpan(b, length).CopyTo(buffer.AsSpan(a, length));
        tmp[..length].CopyTo(buffer.AsSpan(b, length));
    }

    public static byte[] ParseMac(string text)
    {
        try
        {
            var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
            return hex.Length == 12 ? Convert.FromHexString(hex) : [];
        }
        catch
        {
            return [];
        }
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("x2")));
}
