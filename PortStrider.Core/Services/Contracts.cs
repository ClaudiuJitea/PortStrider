using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.Core.Services;

public interface IAdapterService
{
    IReadOnlyList<AdapterInfo> ListAdapters(bool includeDown = true);
    AdapterInfo? Find(string nameOrId);
    InterfaceSnapshot? Snapshot(string adapterId);
}

public interface IPreFlightService
{
    bool HasAdminPrivileges();
    bool IsWindows { get; }
    bool IsLinux { get; }
    Task<IReadOnlyList<DependencyStatus>> CheckAllAsync(CancellationToken cancellationToken = default);
    Task<bool> RepairAsync(string dependencyId, IProgress<string> progress, CancellationToken cancellationToken = default);
}

public interface ICapabilityService
{
    Task<AdapterCapabilities> EvaluateAsync(AdapterInfo adapter, CancellationToken cancellationToken = default);
}

public interface IProfileStore
{
    IReadOnlyList<TestProfile> List();
    TestProfile? Get(Guid id);
    TestProfile? GetDefault();
    void Save(TestProfile profile);
    void Delete(Guid id);
    TestProfile Duplicate(Guid id, string newName);
    Task ExportAsync(TestProfile profile, string path, CancellationToken cancellationToken = default);
    Task<TestProfile> ImportAsync(string path, CancellationToken cancellationToken = default);
}

public interface IDiscoveryService
{
    /// <param name="observeVlans">
    /// When true the capture stays open after the first xDP frame (until timeout or cancellation) and
    /// counts 802.1Q tags seen on the port, filling <see cref="SwitchInfo.ObservedVlans"/>.
    /// </param>
    Task<SwitchInfo> SniffSwitchPortAsync(string captureDevice, int timeoutSeconds = 35, bool observeVlans = false, CancellationToken cancellationToken = default);
}

public interface IReflectorService
{
    bool IsRunning { get; }
    ReflectorStatistics Statistics { get; }
    event EventHandler<ReflectorStatistics>? Updated;
    Task StartAsync(string captureDevice, ReflectorOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
}

/// <summary>Applies profile-driven interface settings (MAC, forced link mode, VLAN sub-interface, static IP) and restores them.</summary>
public interface IInterfaceConfigService
{
    bool IsSupported { get; }
    /// <summary>True when the profile asks for something this service would have to change (MAC, forced mode, VLAN, static IP).</summary>
    bool RequiresChanges(TestProfile profile);
    Task<InterfaceConfigSession> ApplyAsync(AdapterInfo adapter, TestProfile profile, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Puts a (DHCP-offered) address and optional gateway on the session's effective interface so later steps route
    /// through it. Undone with the session. Returns an error message or null.
    /// </summary>
    Task<string?> ApplyAddressAsync(InterfaceConfigSession session, string address, string? subnetMask, string? gateway, CancellationToken cancellationToken = default);
    /// <summary>Blinks the switch-port LED by cycling the link at <paramref name="period"/> until cancelled.</summary>
    Task FlashPortAsync(string interfaceName, TimeSpan period, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class InterfaceConfigSession : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _restore;
    private readonly Stack<Func<CancellationToken, Task>> _extraUndo = new();
    private int _restored;

    public InterfaceConfigSession(InterfaceConfigResult result, string baseInterface, string effectiveInterface, Func<CancellationToken, Task> restore)
    {
        Result = result;
        BaseInterface = baseInterface;
        EffectiveInterface = effectiveInterface;
        _restore = restore;
    }

    public InterfaceConfigResult Result { get; }
    public string BaseInterface { get; }
    public string EffectiveInterface { get; }
    public bool HasChanges => Result.Applied.Count > 0 || _extraUndo.Count > 0;
    public bool IsSubInterface => !string.Equals(BaseInterface, EffectiveInterface, StringComparison.Ordinal);

    /// <summary>Registers an additional rollback action (run before the primary restore, LIFO).</summary>
    public void AddUndo(Func<CancellationToken, Task> undo) => _extraUndo.Push(undo);

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0) return;
        while (_extraUndo.Count > 0)
        {
            try { await _extraUndo.Pop()(cancellationToken); }
            catch { /* best effort */ }
        }

        await _restore(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await RestoreAsync();
}

public interface IDot1xService
{
    bool IsSupported { get; }
    /// <summary>Starts a supplicant on the interface and waits for EAP success/failure. The session keeps the port authorized until disposed.</summary>
    Task<Dot1xSession> AuthenticateAsync(string interfaceName, TestProfile profile, TimeSpan timeout, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class Dot1xSession : IAsyncDisposable
{
    private readonly Func<Task> _stop;
    private int _stopped;

    public Dot1xSession(Dot1xResult result, Func<Task> stop)
    {
        Result = result;
        _stop = stop;
    }

    public Dot1xResult Result { get; }
    public Task StopAsync() => Interlocked.Exchange(ref _stopped, 1) == 0 ? _stop() : Task.CompletedTask;
    public async ValueTask DisposeAsync() => await StopAsync();
}

public interface IVlanMonitorService
{
    bool IsRunning { get; }
    IReadOnlyList<VlanObservation> Current { get; }
    event EventHandler<IReadOnlyList<VlanObservation>>? Updated;
    Task StartAsync(string captureDevice, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IAutoTestService
{
    Task<SessionReport> ExecuteAsync(AdapterInfo adapter, TestProfile profile, IProgress<TestStepUpdate> progress, CancellationToken cancellationToken = default);
}

public sealed class CaptureOptions
{
    public string? BpfFilter { get; init; }
    public string? OutputPath { get; init; }
    public long MaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int SnapLength { get; init; }
    public bool AllowJumboFrames { get; init; } = true;
}

public sealed class CaptureStatistics
{
    public long FramesCaptured { get; init; }
    public long BytesCaptured { get; init; }
    public long FramesDropped { get; init; }
    public long UiFramesTrimmed { get; init; }
    public TimeSpan Duration { get; init; }
    public string? OutputPath { get; init; }
    public IReadOnlyDictionary<string, long> ProtocolCounts { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<VlanObservation> TopVlans { get; init; } = [];
}

public interface ICaptureService
{
    bool IsRunning { get; }
    CaptureStatistics Statistics { get; }
    event EventHandler<CapturedFrame>? FrameArrived;
    Task StartAsync(string captureDevice, CaptureOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
    Task SavePcapAsync(string path, IReadOnlyList<CapturedFrame> frames, CancellationToken cancellationToken = default);
}

public interface IProbeService
{
    Task<PingSample> PingOnceAsync(string host, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TraceHop>> TracerouteAsync(string host, IProgress<TraceHop> progress, CancellationToken cancellationToken = default);
    Task<PortProbeResult> ProbePortAsync(string host, int port, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default);
    Task<HttpProbeResult> ProbeHttpAsync(string url, string? proxyUrl = null, CancellationToken cancellationToken = default);
    /// <summary>Sends a UDP A-record query directly to <paramref name="server"/> and times the response.</summary>
    Task<PingSample> QueryDnsAsync(string server, string name, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default);
}

public interface IDhcpTestService
{
    Task<DhcpTestResult> DiscoverAsync(AdapterInfo adapter, TestProfile profile, bool allowRenew, CancellationToken cancellationToken = default);
}

public interface ILinkTelemetryService
{
    Task<LinkTelemetry> ReadAsync(AdapterInfo adapter, CancellationToken cancellationToken = default);
    Task<PoeMeasurement> ReadPoeAsync(AdapterInfo adapter, SwitchInfo? switchInfo, CancellationToken cancellationToken = default);
}

public interface ICableTestService
{
    Task<CableTestResult> RunAsync(string adapterName, CableUnit unit, IProgress<string> progress, CancellationToken cancellationToken = default);
    FeatureCapability WiremapCapability();
    FeatureCapability ToneCapability();
}

public interface ISfpDiagnosticsService
{
    Task<SfpDiagnostics> ReadAsync(string adapterName, CancellationToken cancellationToken = default);
}

public interface IIperfService
{
    Task<IperfResult> RunClientAsync(string host, int seconds, int port, IProgress<string> progress, CancellationToken cancellationToken = default);
    Task StartServerAsync(int port, IProgress<string> progress, CancellationToken cancellationToken = default);
    Task StopServerAsync();
    bool IsServerRunning { get; }
}

public interface INeighborService
{
    Task<IReadOnlyList<NeighborEntry>> ListAsync(string? adapterName, CancellationToken cancellationToken = default);
}

public interface IReportStore
{
    IReadOnlyList<SessionReport> List(string? search = null);
    SessionReport? Get(Guid id);
    void Add(SessionReport report);
    void Delete(Guid id);
    SessionReport Duplicate(Guid id);
    Task AddAttachmentAsync(Guid reportId, string sourcePath, CancellationToken cancellationToken = default);
    Task ExportJsonAsync(SessionReport report, string path, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(SessionReport report, string path, CancellationToken cancellationToken = default);
    Task ExportPdfAsync(SessionReport report, string path, CancellationToken cancellationToken = default);
    Task ExportBundleAsync(SessionReport report, string zipPath, CancellationToken cancellationToken = default);
    Task<SessionReport> ImportBundleAsync(string zipPath, CancellationToken cancellationToken = default);
}
