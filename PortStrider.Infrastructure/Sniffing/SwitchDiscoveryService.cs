using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PortStrider.Infrastructure.Sniffing;

public sealed class SwitchDiscoveryService : IDiscoveryService
{
    private const string DefaultFilter =
        "ether proto 0x88cc or ether proto 0x88b6 or ether dst 01:80:c2:00:00:0e or ether dst 01:00:0c:cc:cc:cc or (vlan and (ether proto 0x88cc or ether proto 0x88b6))";

    public async Task<SwitchInfo> SniffSwitchPortAsync(string captureDevice, int timeoutSeconds = 35, bool observeVlans = false, CancellationToken cancellationToken = default)
    {
        var device = FindDevice(captureDevice)
            ?? throw new ArgumentException($"Capture device '{captureDevice}' was not found. Is libpcap/Npcap installed?");
        using var lease = CaptureDeviceLease.Acquire(device.Name);

        var builder = new SwitchInfoBuilder();
        var tcs = new TaskCompletionSource<SwitchInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnArrival(object sender, PacketCapture e)
        {
            try
            {
                var raw = e.GetPacket();
                var data = raw.Data;
                if (observeVlans && TagVid(data) is > 0 and var vid)
                    builder.AddObservedVlan(vid);

                var decoded = TryDecode(data, builder);
                // In observe mode keep the capture open for the whole window so the VLAN "Seen / VIDs" card is meaningful.
                if (decoded && !observeVlans)
                    tcs.TrySetResult(builder.Build());
            }
            catch
            {
                // Keep listening on malformed frames.
            }
        }

        try
        {
            device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.Promiscuous,
                ReadTimeout = 1000
            });
        }
        catch (Exception)
        {
            device.Open(DeviceModes.Promiscuous, 1000);
        }

        try
        {
            try { device.Filter = observeVlans ? DefaultFilter + " or vlan" : DefaultFilter; }
            catch { /* software-filter in TryDecode */ }

            device.OnPacketArrival += OnArrival;
            device.StartCapture();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)));
            using var reg = linked.Token.Register(() => tcs.TrySetResult(builder.Protocol == DiscoveryProtocol.None
                ? TimeoutInfo(builder)
                : builder.Build()));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            try { device.OnPacketArrival -= OnArrival; } catch { /* ignore */ }
            try { device.StopCapture(); } catch { /* ignore */ }
            try { device.Close(); } catch { /* ignore */ }
        }
    }

    internal static bool TryDecode(byte[] data, SwitchInfoBuilder builder)
    {
        if (data.Length < 14) return false;
        var dest = data.AsSpan(0, 6);
        var ethertype = (data[12] << 8) | data[13];
        var payloadOffset = 14;

        if (ethertype == 0x8100 && data.Length >= 18)
        {
            ethertype = (data[16] << 8) | data[17];
            payloadOffset = 18;
        }

        if (ethertype == 0x88CC && payloadOffset < data.Length)
        {
            LldpParser.Parse(data.AsSpan(payloadOffset), builder);
            return builder.Protocol == DiscoveryProtocol.Lldp;
        }

        if (EdpParser.IsEdpFrame(data))
        {
            EdpParser.Parse(data, builder);
            return builder.Protocol == DiscoveryProtocol.Edp;
        }

        if (IsCdpDest(dest))
        {
            CdpParser.Parse(data.AsSpan(14), builder);
            return builder.Protocol == DiscoveryProtocol.Cdp;
        }

        return false;
    }

    internal static int? TagVid(byte[] data)
    {
        if (data.Length < 18) return null;
        var ethertype = (data[12] << 8) | data[13];
        if (ethertype is not (0x8100 or 0x88A8)) return null;
        return ((data[14] & 0x0F) << 8) | data[15];
    }

    private static bool IsCdpDest(ReadOnlySpan<byte> dest) =>
        dest.Length >= 6
        && dest[0] == 0x01 && dest[1] == 0x00 && dest[2] == 0x0C
        && dest[3] == 0xCC && dest[4] == 0xCC && dest[5] == 0xCC;

    private static ILiveDevice? FindDevice(string name)
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            if (device.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (device.Description?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return device;
            }

            if (device is LibPcapLiveDevice live
                && (live.Interface.FriendlyName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return device;
            }
        }

        return CaptureDeviceList.Instance.FirstOrDefault();
    }

    private static SwitchInfo TimeoutInfo(SwitchInfoBuilder builder) => new()
    {
        SwitchName = "No LLDP/CDP received",
        PortId = "—",
        Model = "Switch may have discovery disabled, or capture privileges are missing",
        ObservedVlans = builder.ObservedVlans
    };
}
