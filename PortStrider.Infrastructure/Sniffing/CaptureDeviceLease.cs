using System.Collections.Concurrent;

namespace PortStrider.Infrastructure.Sniffing;

internal static class CaptureDeviceLease
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Acquire(string deviceName)
    {
        var gate = Gates.GetOrAdd(deviceName, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
            throw new InvalidOperationException(
                "This adapter is already being used by another capture, VLAN monitor, or switch discovery session.");
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
