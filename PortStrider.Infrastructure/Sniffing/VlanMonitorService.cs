using System.Collections.Concurrent;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using SharpPcap;

namespace PortStrider.Infrastructure.Sniffing;

public sealed class VlanMonitorService : IVlanMonitorService, IDisposable
{
    private ILiveDevice? _device;
    private IDisposable? _lease;
    private PacketArrivalEventHandler? _handler;
    private readonly ConcurrentDictionary<int, (long frames, long bytes)> _counts = new();

    public bool IsRunning { get; private set; }
    public IReadOnlyList<VlanObservation> Current => BuildSnapshot();
    public event EventHandler<IReadOnlyList<VlanObservation>>? Updated;

    public Task StartAsync(string captureDevice, CancellationToken cancellationToken = default)
    {
        StopInternal();
        _counts.Clear();
        var device = FindDevice(captureDevice)
            ?? throw new InvalidOperationException($"Capture device '{captureDevice}' not found.");
        _lease = CaptureDeviceLease.Acquire(device.Name);

        try
        {
            device.Open(DeviceModes.Promiscuous, 500);
            _handler = (_, e) =>
            {
                var raw = e.GetPacket().Data;
                var vlan = ExtractVlan(raw);
                if (vlan is null) return;
                _counts.AddOrUpdate(vlan.Value, _ => (1, raw.Length), (_, v) => (v.frames + 1, v.bytes + raw.Length));
                Updated?.Invoke(this, BuildSnapshot());
            };
            device.OnPacketArrival += _handler;
            device.StartCapture();
            _device = device;
            IsRunning = true;
            return Task.CompletedTask;
        }
        catch
        {
            try { device.Close(); } catch { /* ignore cleanup failure */ }
            _handler = null;
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public Task StopAsync()
    {
        StopInternal();
        return Task.CompletedTask;
    }

    private IReadOnlyList<VlanObservation> BuildSnapshot()
    {
        var total = _counts.Values.Sum(v => v.frames);
        return _counts
            .OrderByDescending(kv => kv.Value.frames)
            .Take(9)
            .Select(kv => new VlanObservation
            {
                VlanId = kv.Key,
                Frames = kv.Value.frames,
                Bytes = kv.Value.bytes,
                SharePercent = total == 0 ? 0 : kv.Value.frames * 100d / total
            })
            .ToArray();
    }

    private static int? ExtractVlan(byte[] data)
    {
        if (data.Length < 16) return null;
        var ethertype = (data[12] << 8) | data[13];
        if (ethertype is 0x8100 or 0x88A8 && data.Length >= 18)
            return (data[14] & 0x0F) << 8 | data[15];
        return null;
    }

    private static ILiveDevice? FindDevice(string name)
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            if (device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (device.Description?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
                return device;
        }

        return CaptureDeviceList.Instance.FirstOrDefault();
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
            // ignore
        }

        _device = null;
        _handler = null;
        _lease?.Dispose();
        _lease = null;
    }

    public void Dispose() => StopInternal();
}
