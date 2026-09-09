using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Probes;

namespace PortStrider.UI.ViewModels;

public partial class TestStepViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private TestStatus _status = TestStatus.Pending;
    [ObservableProperty] private string _details = "Waiting…";
    [ObservableProperty] private string? _continuousHost;
    [ObservableProperty] private int? _continuousPort;

    public bool CanContinue => !string.IsNullOrWhiteSpace(ContinuousHost);

    partial void OnContinuousHostChanged(string? value) => OnPropertyChanged(nameof(CanContinue));
}

public partial class AutoTestViewModel : ViewModelBase
{
    private readonly IAutoTestService _autoTest;
    private readonly IDiscoveryService _discovery;
    private readonly IReportStore _reports;
    private readonly IProfileStore _profiles;
    private readonly ILinkTelemetryService _telemetry;
    private readonly IProbeService _probes;
    private readonly IAdapterService _adapters;
    private readonly IInterfaceConfigService _ifconfig;
    private readonly AppSession _session;
    private readonly DispatcherTimer _linkWatch;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _continuousCts;
    private bool? _lastLinkUp;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TestStatus _overall = TestStatus.Pending;
    [ObservableProperty] private string _overallLabel = "READY";
    [ObservableProperty] private string _durationLabel = "—";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private SwitchInfo _switchInfo = SwitchInfo.Idle();
    [ObservableProperty] private string _discoveryStatus = "Idle";
    [ObservableProperty] private bool _isRefreshingSwitch;
    [ObservableProperty] private TestProfile? _selectedProfile;
    [ObservableProperty] private string _jobLabel = "";
    [ObservableProperty] private string _siteLabel = "";
    [ObservableProperty] private string _technicianNotes = "";
    [ObservableProperty] private bool _autoRunOnLink;
    [ObservableProperty] private string _autoRunStatus = "";

    [ObservableProperty] private bool _isContinuous;
    [ObservableProperty] private string _continuousTitle = "";
    [ObservableProperty] private string _continuousStats = "";

    public ObservableCollection<TestStepViewModel> Steps { get; } = new();
    public ObservableCollection<TestProfile> Profiles { get; } = new();
    public ObservableCollection<PingSample> ContinuousSamples { get; } = new();

    public bool HasFailures => FailCount > 0;
    public bool HasWarnings => WarnCount > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    partial void OnFailCountChanged(int value) => OnPropertyChanged(nameof(HasFailures));
    partial void OnWarnCountChanged(int value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    public AutoTestViewModel(
        IAutoTestService autoTest,
        IDiscoveryService discovery,
        IReportStore reports,
        IProfileStore profiles,
        ILinkTelemetryService telemetry,
        IProbeService probes,
        IAdapterService adapters,
        IInterfaceConfigService ifconfig,
        AppSession session)
    {
        _autoTest = autoTest;
        _discovery = discovery;
        _reports = reports;
        _profiles = profiles;
        _telemetry = telemetry;
        _probes = probes;
        _adapters = adapters;
        _ifconfig = ifconfig;
        _session = session;
        ReloadProfiles();
        ResetSteps();

        _linkWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _linkWatch.Tick += (_, _) => WatchLink();
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSession.SelectedAdapter)) _lastLinkUp = null;
        };
    }

    public AppSession Session => _session;

    public void ReloadProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in _profiles.List()) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? _profiles.GetDefault();
    }

    partial void OnSelectedProfileChanged(TestProfile? value)
    {
        if (!IsRunning) ResetSteps();
    }

    partial void OnAutoRunOnLinkChanged(bool value)
    {
        if (value)
        {
            _lastLinkUp = _session.SelectedAdapter?.IsUp;
            AutoRunStatus = "Waiting for link-up on the selected adapter…";
            _linkWatch.Start();
        }
        else
        {
            _linkWatch.Stop();
            AutoRunStatus = "";
        }
    }

    private void WatchLink()
    {
        if (_session.SelectedAdapter is null) return;
        var current = _adapters.Find(_session.SelectedAdapter.Id);
        var up = current?.IsUp ?? false;
        if (_lastLinkUp == false && up && !IsRunning)
        {
            AutoRunStatus = $"Link up on {_session.SelectedAdapter.Name} — running AutoTest";
            _ = AutoRunAsync();
        }
        else if (!up)
        {
            AutoRunStatus = "Link down — AutoTest will start when the cable is connected";
        }

        _lastLinkUp = up;
    }

    private async Task AutoRunAsync()
    {
        // Give the OS DHCP client a moment so the adapter snapshot has an address.
        await Task.Delay(1500);
        await _session.RefreshAdaptersAsync();
        await RunAsync();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_session.SelectedAdapter is null || SelectedProfile is null || IsRunning) return;
        StopContinuous();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        ResetSteps();
        ErrorMessage = "";
        Overall = TestStatus.Running;
        OverallLabel = "RUNNING";
        DiscoveryStatus = "Listening for LLDP/CDP/EDP + VLAN tags…";
        SwitchInfo = SwitchInfo.Idle();
        var started = DateTimeOffset.Now;
        var adapter = _session.SelectedAdapter;
        var profile = SelectedProfile;
        SwitchInfo? discovered = null;

        using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var sniff = Task.Run(
            () => _discovery.SniffSwitchPortAsync(adapter.CaptureDevice, 40, observeVlans: true, discoveryCts.Token),
            discoveryCts.Token);

        SessionReport? report = null;
        var cancelled = false;
        var progress = new Progress<TestStepUpdate>(Apply);
        try
        {
            report = await _autoTest.ExecuteAsync(adapter, profile, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Overall = TestStatus.Skipped;
            OverallLabel = "CANCELLED";
        }
        catch (Exception ex)
        {
            Overall = TestStatus.Fail;
            OverallLabel = "ERROR";
            ErrorMessage = ex.Message;
        }

        // Let discovery run at least a few seconds even on very fast tests so the VLAN "Seen" card has data.
        if (!cancelled && (DateTimeOffset.Now - started) < TimeSpan.FromSeconds(6))
        {
            try { await Task.Delay(TimeSpan.FromSeconds(6) - (DateTimeOffset.Now - started), _cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }

        discoveryCts.Cancel();
        try
        {
            discovered = await sniff;
            SwitchInfo = discovered;
            DiscoveryStatus = discovered.Protocol == DiscoveryProtocol.None
                ? "No neighbor advertisements"
                : $"Decoded {discovered.ProtocolLabel}";
        }
        catch (OperationCanceledException)
        {
            DiscoveryStatus = cancelled ? "Discovery cancelled" : "No neighbor advertisement during test";
        }
        catch (Exception ex)
        {
            DiscoveryStatus = ex.Message;
        }

        VlanTestResult? vlan = report?.Vlan;
        if (!cancelled)
            vlan = ApplyVlanSeen(profile, report?.Vlan, discovered?.ObservedVlans ?? []);

        PoeMeasurement? poe = null;
        if (!cancelled && profile.EnablePoeAdvertised)
        {
            poe = await _telemetry.ReadPoeAsync(adapter, discovered);
            Apply(new TestStepUpdate
            {
                Id = AutoTestRunner.StepPoe,
                Title = "PoE (advertised)",
                Status = poe.Level == CapabilityLevel.Unavailable ? TestStatus.Skipped : TestStatus.Pass,
                Details = poe.Summary
            });
        }
        else if (!cancelled)
        {
            Apply(new TestStepUpdate { Id = AutoTestRunner.StepPoe, Title = "PoE (advertised)", Status = TestStatus.Skipped, Details = "Disabled by profile." });
        }

        DurationLabel = $"{(DateTimeOffset.Now - started).TotalSeconds:0.0}s";
        if (!cancelled && OverallLabel != "ERROR") RecomputeOverall();

        report ??= new SessionReport
        {
            Id = Guid.NewGuid(),
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            AdapterName = adapter.Name,
            ProfileName = profile.Name,
            Overall = OverallLabel,
            Ipv4 = adapter.Ipv4 ?? "",
            Ipv6 = adapter.Ipv6 ?? "",
            Gateway = adapter.Gateway ?? ""
        };

        try
        {
            _reports.Add(report with
            {
                Poe = poe,
                Vlan = vlan,
                ObservedVlans = (discovered?.ObservedVlans ?? []).Select(v => new VlanObservation { VlanId = v }).ToArray(),
                Steps = Steps.Select(s => new TestStepUpdate { Id = s.Id, Title = s.Title, Status = s.Status, Details = s.Details, ContinuousHost = s.ContinuousHost, ContinuousPort = s.ContinuousPort }).ToArray(),
                JobLabel = JobLabel,
                SiteLabel = SiteLabel,
                TechnicianNotes = TechnicianNotes,
                Switch = discovered ?? SwitchInfo,
                Overall = OverallLabel
            });
        }
        catch (Exception ex)
        {
            DiscoveryStatus = $"Report could not be saved: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            if (AutoRunOnLink) AutoRunStatus = "Waiting for the next link-up…";
        }
    }

    private VlanTestResult? ApplyVlanSeen(TestProfile profile, VlanTestResult? configured, IReadOnlyList<int> seen)
    {
        // G2: the VLAN card appears when a VLAN is configured OR tagged traffic was detected during AutoTest.
        if (configured is null && seen.Count == 0) return null;
        var seenText = seen.Count == 0 ? "no tagged frames seen" : $"seen {seen.Count}: {string.Join(", ", seen.Take(12))}{(seen.Count > 12 ? "…" : "")}";
        if (configured is null)
        {
            var info = new VlanTestResult { Status = TestStatus.Pass, Summary = $"Untagged test · {seenText}", SeenVlans = seen };
            Apply(new TestStepUpdate { Id = AutoTestRunner.StepVlan, Title = "VLAN", Status = TestStatus.Pass, Details = info.Summary });
            return info;
        }

        var merged = new VlanTestResult
        {
            ConfiguredVlan = configured.ConfiguredVlan,
            Priority = configured.Priority,
            InterfaceName = configured.InterfaceName,
            Status = configured.Status,
            Summary = $"{configured.Summary} · {seenText}",
            SeenVlans = seen
        };
        Apply(new TestStepUpdate { Id = AutoTestRunner.StepVlan, Title = "VLAN", Status = merged.Status, Details = merged.Summary });
        return merged;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StopContinuous();
    }

    /// <summary>G2 "Refresh xDP": wait for the next LLDP/CDP/EDP advertisement without re-running the whole test.</summary>
    [RelayCommand]
    private async Task RefreshSwitchAsync()
    {
        if (_session.SelectedAdapter is null || IsRunning || IsRefreshingSwitch) return;
        IsRefreshingSwitch = true;
        DiscoveryStatus = "Waiting for the next advertisement (up to 35 s)…";
        try
        {
            var info = await Task.Run(() => _discovery.SniffSwitchPortAsync(_session.SelectedAdapter.CaptureDevice, 35, observeVlans: false));
            SwitchInfo = info;
            DiscoveryStatus = info.Protocol == DiscoveryProtocol.None ? "No neighbor advertisements" : $"Decoded {info.ProtocolLabel}";
            if (SelectedProfile?.EnablePoeAdvertised == true)
            {
                var poe = await _telemetry.ReadPoeAsync(_session.SelectedAdapter, info);
                Apply(new TestStepUpdate
                {
                    Id = AutoTestRunner.StepPoe,
                    Title = "PoE (advertised)",
                    Status = poe.Level == CapabilityLevel.Unavailable ? TestStatus.Skipped : TestStatus.Pass,
                    Details = poe.Summary
                });
            }
        }
        catch (Exception ex)
        {
            DiscoveryStatus = ex.Message;
        }
        finally
        {
            IsRefreshingSwitch = false;
        }
    }

    /// <summary>G2 "CONTINUOUS" on a gateway/target card: keep pinging (or TCP-connecting) until stopped.</summary>
    [RelayCommand]
    private async Task StartContinuousAsync(TestStepViewModel? step)
    {
        if (step is null || !step.CanContinue) return;
        StopContinuous();
        var host = step.ContinuousHost!;
        var port = step.ContinuousPort;
        _continuousCts = new CancellationTokenSource();
        var token = _continuousCts.Token;
        IsContinuous = true;
        ContinuousTitle = port is null ? $"Continuous ping · {host}" : $"Continuous TCP · {host}:{port}";
        ContinuousSamples.Clear();
        var sent = 0; var recv = 0; var times = new List<long>(); var seq = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                PingSample sample;
                if (port is null)
                {
                    sample = await _probes.PingOnceAsync(host, 1000, null, token);
                }
                else
                {
                    var probe = await _probes.ProbePortAsync(host, port.Value, 1000, null, token);
                    sample = new PingSample { Sequence = 0, Success = probe.Open, RoundtripMs = probe.ElapsedMs, Status = probe.Open ? "open" : probe.Error ?? "closed" };
                }

                sample = sample with { Sequence = ++seq };
                sent++;
                if (sample.Success) { recv++; times.Add(sample.RoundtripMs); }
                ContinuousSamples.Insert(0, sample);
                while (ContinuousSamples.Count > 40) ContinuousSamples.RemoveAt(ContinuousSamples.Count - 1);
                var loss = sent == 0 ? 0 : (sent - recv) * 100d / sent;
                ContinuousStats = times.Count == 0
                    ? $"{recv}/{sent} · {loss:0.0}% loss"
                    : $"{recv}/{sent} · {loss:0.0}% loss · min {times.Min()} / avg {times.Average():0.0} / max {times.Max()} ms";
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            IsContinuous = false;
        }
    }

    [RelayCommand]
    private void StopContinuous()
    {
        _continuousCts?.Cancel();
        _continuousCts = null;
    }

    private void Apply(TestStepUpdate update)
    {
        var step = Steps.FirstOrDefault(s => s.Id == update.Id);
        if (step is null)
        {
            Steps.Add(new TestStepViewModel
            {
                Id = update.Id, Title = update.Title, Status = update.Status, Details = update.Details,
                ContinuousHost = update.ContinuousHost, ContinuousPort = update.ContinuousPort
            });
        }
        else
        {
            step.Title = update.Title;
            step.Status = update.Status;
            step.Details = update.Details;
            if (update.ContinuousHost is not null)
            {
                step.ContinuousHost = update.ContinuousHost;
                step.ContinuousPort = update.ContinuousPort;
            }
        }

        RecomputeOverall();
    }

    private void RecomputeOverall()
    {
        FailCount = Steps.Count(s => s.Status == TestStatus.Fail);
        WarnCount = Steps.Count(s => s.Status == TestStatus.Warning);
        if (FailCount > 0)
        {
            Overall = TestStatus.Fail;
            OverallLabel = "FAIL";
        }
        else if (WarnCount > 0)
        {
            Overall = TestStatus.Warning;
            OverallLabel = "WARN";
        }
        else if (Steps.All(s => s.Status is TestStatus.Pass or TestStatus.Skipped))
        {
            Overall = TestStatus.Pass;
            OverallLabel = "PASS";
        }
        else if (Steps.Any(s => s.Status == TestStatus.Running))
        {
            Overall = TestStatus.Running;
            OverallLabel = "RUNNING";
        }
    }

    private void ResetSteps()
    {
        Steps.Clear();
        var profile = SelectedProfile;
        if (profile is not null && _ifconfig.RequiresChanges(profile))
            AddPendingStep(AutoTestRunner.StepConfig, "Interface configuration");
        AddPendingStep(AutoTestRunner.StepLink, "Physical link");
        AddPendingStep(AutoTestRunner.StepIdentity, "Interface identity");
        if (profile?.Enable8021X == true)
            AddPendingStep(AutoTestRunner.StepDot1x, "802.1X authentication");
        AddPendingStep(AutoTestRunner.StepDhcp, "DHCP");
        AddPendingStep(AutoTestRunner.StepAddressing, "Addressing");
        if (profile?.VlanId is > 0)
            AddPendingStep(AutoTestRunner.StepVlan, "VLAN");
        if (profile?.EnableIpv6 == true)
            AddPendingStep(AutoTestRunner.StepIpv6, "IPv6 connectivity");
        AddPendingStep(AutoTestRunner.StepGateway, "Default gateway");
        AddPendingStep(AutoTestRunner.StepDns, "DNS servers");
        if (profile is not null)
        {
            foreach (var target in profile.Targets.Where(target => target.Enabled))
                AddPendingStep($"target-{target.Id}", target.Label);
            if (profile.EnablePoeAdvertised)
                AddPendingStep(AutoTestRunner.StepPoe, "PoE (advertised)");
        }

        FailCount = 0;
        WarnCount = 0;
        Overall = TestStatus.Pending;
        OverallLabel = "READY";
        DurationLabel = "—";
    }

    private void AddPendingStep(string id, string title)
    {
        if (Steps.Any(step => step.Id == id)) return;
        Steps.Add(new TestStepViewModel { Id = id, Title = title, Status = TestStatus.Pending, Details = "Not run yet" });
    }
}
