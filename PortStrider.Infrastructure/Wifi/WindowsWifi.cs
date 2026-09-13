using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Wifi;

// Native WLAN avoids localized netsh output and preserves the driver's actual RSSI in dBm.
internal static class WindowsWifi
{
    public static async Task<WifiScan> ScanAsync(string adapterId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(adapterId, out var id)) throw new InvalidOperationException("The WiFi adapter has no valid Windows interface GUID.");
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NotificationCallback callback = (ref Notification data, IntPtr context) =>
        {
            if (data.Source != 8 || data.InterfaceGuid != id) return;
            if (data.Code == 7) completed.TrySetResult(true); // wlan_notification_acm_scan_complete
            if (data.Code == 8) completed.TrySetException(new InvalidOperationException("The WiFi driver could not complete the scan. Check the radio and try again."));
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(WlanRegisterNotification(handle, 8, false, callback, IntPtr.Zero, IntPtr.Zero, out _));
            Check(WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            var networks = ReadNetworks(handle, id);
            cancellationToken.ThrowIfCancellationRequested();
            return new WifiScan(DateTimeOffset.Now, networks);
        }
        finally
        {
            WlanRegisterNotification(handle, 0, false, null, IntPtr.Zero, IntPtr.Zero, out _);
            WlanCloseHandle(handle, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
    }

    public static string CheckReadiness()
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle));
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var list));
            try
            {
                var count = Marshal.ReadInt32(list);
                if (count == 0) throw new InvalidOperationException("No WiFi adapter found. Connect an adapter and install its driver.");
                // Querying BSS lists checks radio state and location access without starting an active scan.
                Exception? error = null;
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<InterfaceInfo>(IntPtr.Add(list, 8 + i * Marshal.SizeOf<InterfaceInfo>()));
                    try { ReadNetworks(handle, info.Id); return $"Native WLAN ready · {info.Description}"; }
                    catch (Exception ex) { error = ex; }
                }
                throw error ?? new InvalidOperationException("No WiFi radio is available.");
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    private static IReadOnlyList<WifiNetwork> ReadNetworks(IntPtr handle, Guid id)
    {
        Check(WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out var list));
        try
        {
            var bytes = Marshal.ReadInt32(list);
            var count = Marshal.ReadInt32(list, 4);
            var size = Marshal.SizeOf<BssEntry>();
            if (bytes < 8 || count < 0 || count > (bytes - 8) / size)
                throw new InvalidOperationException("The WiFi driver returned an invalid BSS list.");
            var networks = new List<WifiNetwork>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<BssEntry>(IntPtr.Add(list, 8 + i * size));
                var frequency = (int)(entry.FrequencyKhz / 1000);
                if (entry.SsidLength > 32 || entry.Rssi is < -127 or > 0 || WifiChannels.Channel(frequency) == 0) continue;
                networks.Add(new WifiNetwork(Encoding.UTF8.GetString(entry.Ssid, 0, (int)entry.SsidLength).TrimEnd('\0'),
                    string.Join(":", entry.Bssid.Select(b => b.ToString("X2"))), frequency, entry.Rssi,
                    (entry.Capability & 0x10) != 0 ? "Protected" : "Open"));
            }
            return networks.GroupBy(n => n.Key).Select(g => g.OrderByDescending(n => n.SignalDbm).First())
                .OrderByDescending(n => n.SignalDbm).ToArray();
        }
        finally { WlanFreeMemory(list); }
    }

    private static void Check(uint code)
    {
        if (code == 0) return;
        var message = code switch
        {
            5 => "WiFi access denied. Enable Location services and desktop app location access in Windows Settings, then retry.",
            1062 => "Start the Windows WLAN AutoConfig service to scan WiFi.",
            0x80342002 => "The WiFi radio is off. Enable WiFi and turn off airplane mode.",
            _ => $"Windows WiFi: {new Win32Exception(unchecked((int)code)).Message} ({code}). Check the adapter driver and WLAN AutoConfig service."
        };
        throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BssEntry
    {
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
        public uint PhyId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public uint BssType, PhyType;
        public int Rssi;
        public uint LinkQuality;
        public byte InRegDomain;
        public ushort BeaconPeriod;
        public ulong Timestamp, HostTimestamp;
        public ushort Capability;
        public uint FrequencyKhz;
        public uint RateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] Rates;
        public uint IeOffset, IeSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Notification
    {
        public uint Source, Code;
        public Guid InterfaceGuid;
        public uint DataSize;
        public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NotificationCallback(ref Notification data, IntPtr context);
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid id, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid id, IntPtr ssid, uint type,
        [MarshalAs(UnmanagedType.Bool)] bool security, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(IntPtr handle, uint source,
        [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, NotificationCallback? callback, IntPtr context, IntPtr reserved, out uint previous);
}
