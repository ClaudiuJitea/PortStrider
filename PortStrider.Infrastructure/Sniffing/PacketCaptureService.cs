using System.Collections.Concurrent;
using System.Diagnostics;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PortStrider.Infrastructure.Sniffing;

public sealed class PacketCaptureService : ICaptureService, IDisposable
{
    private ILiveDevice? _device;
    private IDisposable? _lease;
    private CaptureFileWriterDevice? _writer;
    private long _number;
    private long _bytesCaptured;
    private long _framesDropped;
    private long _uiTrimmed;
    private PacketArrivalEventHandler? _handler;
    private DateTimeOffset _startedAt;
    private readonly ConcurrentDictionary<string, long> _protocolCounts = new();
    private readonly ConcurrentDictionary<int, (long frames, long bytes)> _vlanCounts = new();
    private CaptureOptions _options = new();
    private string? _outputPath;

    public bool IsRunning { get; private set; }
    public CaptureStatistics Statistics => new()
    {
        FramesCaptured = _number,
        BytesCaptured = _bytesCaptured,
        FramesDropped = _framesDropped,
        UiFramesTrimmed = _uiTrimmed,
        Duration = IsRunning ? DateTimeOffset.Now - _startedAt : TimeSpan.Zero,
        OutputPath = _outputPath,
        ProtocolCounts = _protocolCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
        TopVlans = _vlanCounts.OrderByDescending(kv => kv.Value.frames).Take(9)
            .Select(kv => new VlanObservation { VlanId = kv.Key, Frames = kv.Value.frames, Bytes = kv.Value.bytes })
            .ToArray()
    };

    public event EventHandler<CapturedFrame>? FrameArrived;

    public Task StartAsync(string captureDevice, CaptureOptions options, CancellationToken cancellationToken = default)
    {
        StopInternal();
        _number = 0;
        _bytesCaptured = 0;
        _framesDropped = 0;
        _uiTrimmed = 0;
        _protocolCounts.Clear();
        _vlanCounts.Clear();
        _options = options;
        _outputPath = options.OutputPath;
        _startedAt = DateTimeOffset.Now;

        var selected = FindDevice(captureDevice)
            ?? throw new InvalidOperationException("No capture devices available.");
        _lease = CaptureDeviceLease.Acquire(selected.Name);

        try
        {
            selected.Open(new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 500, Snaplen = options.SnapLength > 0 ? options.SnapLength : 65535 });
            if (!string.IsNullOrWhiteSpace(options.BpfFilter))
            {
                try { selected.Filter = options.BpfFilter; }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Invalid capture filter: {ex.Message}", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(_outputPath))
            {
                _writer = new CaptureFileWriterDevice(_outputPath);
                _writer.Open();
            }

            _handler = (_, e) =>
            {
                try
                {
                    var raw = e.GetPacket();
                    var data = raw.Data;
                    if (_options.SnapLength > 0 && data.Length > _options.SnapLength)
                        data = data.AsSpan(0, _options.SnapLength).ToArray();

                    if (_bytesCaptured + data.Length > _options.MaxBytes)
                    {
                        Interlocked.Increment(ref _framesDropped);
                        return;
                    }

                    Interlocked.Add(ref _bytesCaptured, data.Length);
                    var n = Interlocked.Increment(ref _number);
                    if (_writer is not null)
                    {
                        var ts = new PosixTimeval((ulong)raw.Timeval.Seconds, (ulong)raw.Timeval.MicroSeconds);
                        _writer.Write(new RawCapture(LinkLayers.Ethernet, ts, data));
                    }

                    var frame = Decode(n, raw.Timeval.Date.ToUniversalTime(), data);
                    _protocolCounts.AddOrUpdate(frame.Protocol, 1, (_, v) => v + 1);
                    TrackVlan(data);
                    FrameArrived?.Invoke(this, frame);
                }
                catch
                {
                    Interlocked.Increment(ref _framesDropped);
                }
            };

            selected.OnPacketArrival += _handler;
            selected.StartCapture();
            _device = selected;
            IsRunning = true;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            try { selected.Close(); } catch { /* ignore cleanup failure */ }
            try { _writer?.Close(); } catch { /* ignore cleanup failure */ }
            _writer = null;
            _handler = null;
            _lease.Dispose();
            _lease = null;
            throw ToCaptureStartException(ex, captureDevice);
        }
    }

    public Task StopAsync()
    {
        StopInternal();
        return Task.CompletedTask;
    }

    public Task SavePcapAsync(string path, IReadOnlyList<CapturedFrame> frames, CancellationToken cancellationToken = default)
    {
        using var writer = new CaptureFileWriterDevice(path);
        writer.Open();
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ts = new PosixTimeval((ulong)frame.Timestamp.ToUnixTimeSeconds(), (ulong)frame.Timestamp.Millisecond * 1000);
            writer.Write(new RawCapture(LinkLayers.Ethernet, ts, frame.Raw));
        }

        writer.Close();
        return Task.CompletedTask;
    }

    public void NotifyUiTrimmed(int count) => Interlocked.Add(ref _uiTrimmed, count);

    private void TrackVlan(byte[] data)
    {
        if (data.Length < 16) return;
        var ethertype = (data[12] << 8) | data[13];
        var vlan = ethertype is 0x8100 or 0x88A8 ? ((data[14] & 0x0F) << 8 | data[15]) : 0;
        _vlanCounts.AddOrUpdate(vlan, _ => (1, data.Length), (_, v) => (v.frames + 1, v.bytes + data.Length));
    }

    private static ILiveDevice? FindDevice(string name)
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            if (device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (device.Description?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
                return device;
        }

        return null;
    }

    private static Exception ToCaptureStartException(Exception exception, string captureDevice)
    {
        var message = exception.Message;
        if (OperatingSystem.IsLinux()
            && (message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
                || message.Contains("CAP_NET_", StringComparison.OrdinalIgnoreCase)))
        {
            return new UnauthorizedAccessException(
                "Packet capture permission was denied. Open Pre-flight and repair Capture privileges, then try again.",
                exception);
        }

        return new InvalidOperationException(
            $"Could not start capture on {captureDevice}: {message}",
            exception);
    }

    private void StopInternal()
    {
        IsRunning = false;
        if (_device is null)
        {
            _lease?.Dispose();
            _lease = null;
            return;
        }
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

        try { _writer?.Close(); } catch { /* ignore */ }
        _device = null;
        _handler = null;
        _writer = null;
        _lease?.Dispose();
        _lease = null;
    }

    internal static CapturedFrame Decode(long number, DateTime timestamp, byte[] data)
    {
        var frame = new CapturedFrame
        {
            Number = number,
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            Length = data.Length,
            Raw = data
        };

        try
        {
            var packet = Packet.ParsePacket(LinkLayers.Ethernet, data);
            var eth = packet.Extract<EthernetPacket>();
            if (eth is null) return frame with { Protocol = "Ethernet", Summary = $"{data.Length} bytes" };

            var srcMac = eth.SourceHardwareAddress.ToString();
            var dstMac = eth.DestinationHardwareAddress.ToString();
            var type = $"0x{(int)eth.Type:X4}";
            var ip = packet.Extract<IPPacket>();
            var tcp = packet.Extract<TcpPacket>();
            var udp = packet.Extract<UdpPacket>();
            string proto = eth.Type.ToString();
            string summary = $"{eth.Type} {data.Length}B";
            string src = srcMac;
            string dst = dstMac;

            if (ip is not null)
            {
                src = ip.SourceAddress.ToString();
                dst = ip.DestinationAddress.ToString();
                proto = ip.Protocol.ToString();
                summary = $"{ip.Protocol} {src} → {dst}";
            }

            if (tcp is not null)
            {
                proto = "TCP";
                summary = $"TCP {src}:{tcp.SourcePort} → {dst}:{tcp.DestinationPort}";
                src = $"{src}:{tcp.SourcePort}";
                dst = $"{dst}:{tcp.DestinationPort}";
            }
            else if (udp is not null)
            {
                proto = "UDP";
                summary = $"UDP {src}:{udp.SourcePort} → {dst}:{udp.DestinationPort}";
                src = $"{src}:{udp.SourcePort}";
                dst = $"{dst}:{udp.DestinationPort}";
            }
            else if ((int)eth.Type == 0x88CC)
            {
                proto = "LLDP";
                summary = "Link Layer Discovery Protocol";
            }
            else if (dstMac.Replace(":", "").StartsWith("01000C", StringComparison.OrdinalIgnoreCase))
            {
                proto = "CDP";
                summary = "Cisco Discovery Protocol";
            }

            return frame with
            {
                SourceMac = srcMac,
                DestinationMac = dstMac,
                EtherType = type,
                Protocol = proto,
                Summary = summary,
                Source = src,
                Destination = dst
            };
        }
        catch
        {
            return frame with { Protocol = "Unknown", Summary = $"{data.Length} bytes" };
        }
    }

    public void Dispose() => StopInternal();
}
