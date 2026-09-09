using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class ToolsViewModel : ViewModelBase
{
    private readonly IProbeService _probes;
    private readonly IIperfService _iperf;
    private readonly AppSession _session;
    private CancellationTokenSource? _pingCts;
    private int _seq;

    [ObservableProperty] private string _pingHost = "1.1.1.1";
    [ObservableProperty] private bool _isPinging;
    [ObservableProperty] private string _pingStats = "Idle";

    [ObservableProperty] private string _traceHost = "1.1.1.1";
    [ObservableProperty] private bool _isTracing;

    [ObservableProperty] private string _portHost = "";
    [ObservableProperty] private string _portList = "22, 53, 80, 443, 445, 3389, 8080";
    [ObservableProperty] private bool _isProbingPorts;

    [ObservableProperty] private string _httpUrl = "https://1.1.1.1";
    [ObservableProperty] private HttpProbeResult? _httpResult;
    [ObservableProperty] private bool _isHttp;
    [ObservableProperty] private bool _hasHttpResult;

    [ObservableProperty] private string _iperfHost = "";
    [ObservableProperty] private int _iperfSeconds = 10;
    [ObservableProperty] private int _iperfPort = 5201;
    [ObservableProperty] private string _iperfLog = "";
    [ObservableProperty] private bool _isIperf;
    [ObservableProperty] private bool _isIperfServer;
    [ObservableProperty] private string _pingButtonLabel = "PING";
    [ObservableProperty] private string _serverButtonLabel = "START SERVER";

    public ObservableCollection<PingSample> PingSamples { get; } = new();
    public ObservableCollection<TraceHop> Hops { get; } = new();
    public ObservableCollection<PortProbeResult> Ports { get; } = new();
    public AppSession Session => _session;

    public ToolsViewModel(IProbeService probes, IIperfService iperf, AppSession session)
    {
        _probes = probes;
        _iperf = iperf;
        _session = session;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSession.SelectedAdapter) && _session.SelectedAdapter is { } a)
            {
                if (string.IsNullOrEmpty(PortHost)) PortHost = a.Gateway ?? a.Ipv4 ?? "";
                if (string.IsNullOrEmpty(IperfHost)) IperfHost = a.Gateway ?? "";
            }
        };
    }

    [RelayCommand]
    private async Task TogglePingAsync()
    {
        if (IsPinging)
        {
            _pingCts?.Cancel();
            IsPinging = false;
            PingButtonLabel = "PING";
            return;
        }

        IsPinging = true;
        PingButtonLabel = "STOP";
        PingSamples.Clear();
        _seq = 0;
        _pingCts = new CancellationTokenSource();
        var sent = 0;
        var recv = 0;
        var times = new List<long>();
        try
        {
            while (!_pingCts.IsCancellationRequested)
            {
                var sample = await _probes.PingOnceAsync(PingHost, 1000, null, _pingCts.Token);
                sample = sample with { Sequence = ++_seq };
                sent++;
                if (sample.Success)
                {
                    recv++;
                    times.Add(sample.RoundtripMs);
                }

                PingSamples.Add(sample);
                while (PingSamples.Count > 200) PingSamples.RemoveAt(0);
                var loss = sent == 0 ? 0 : (sent - recv) * 100d / sent;
                var avg = times.Count == 0 ? 0 : times.Average();
                PingStats = $"{recv}/{sent} received · {loss:0.0}% loss · min {(times.Count == 0 ? 0 : times.Min())} / avg {avg:0.0} / max {(times.Count == 0 ? 0 : times.Max())} ms";
                await Task.Delay(1000, _pingCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            IsPinging = false;
            PingButtonLabel = "PING";
        }
    }

    [RelayCommand]
    private async Task TraceAsync()
    {
        if (IsTracing) return;
        IsTracing = true;
        Hops.Clear();
        var progress = new Progress<TraceHop>(h => Hops.Add(h));
        try
        {
            await _probes.TracerouteAsync(TraceHost, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Hops.Add(new TraceHop { Ttl = 0, Address = ex.Message, TimedOut = true });
        }
        finally
        {
            IsTracing = false;
        }
    }

    [RelayCommand]
    private async Task ProbePortsAsync()
    {
        if (IsProbingPorts) return;
        var host = string.IsNullOrWhiteSpace(PortHost) ? _session.SelectedAdapter?.Gateway : PortHost;
        if (string.IsNullOrWhiteSpace(host)) return;
        IsProbingPorts = true;
        Ports.Clear();
        var ports = PortList.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n is > 0 and < 65536)
            .ToArray();
        try
        {
            var results = await Task.WhenAll(ports.Select(port => _probes.ProbePortAsync(host, port, 800)));
            foreach (var result in results) Ports.Add(result);
        }
        catch (Exception ex)
        {
            Ports.Add(new PortProbeResult { Port = 0, Service = "Error", Open = false, ElapsedMs = 0, Error = ex.Message });
        }
        finally
        {
            IsProbingPorts = false;
        }
    }

    [RelayCommand]
    private async Task ProbeHttpAsync()
    {
        IsHttp = true;
        try
        {
            HttpResult = await _probes.ProbeHttpAsync(HttpUrl);
            HasHttpResult = HttpResult is not null;
        }
        finally { IsHttp = false; }
    }

    [RelayCommand]
    private async Task RunIperfAsync()
    {
        if (string.IsNullOrWhiteSpace(IperfHost)) return;
        IsIperf = true;
        IperfLog = "Starting…";
        var progress = new Progress<string>(m => IperfLog = m);
        try
        {
            var result = await _iperf.RunClientAsync(IperfHost, IperfSeconds, IperfPort, progress);
            IperfLog = result.Error ?? result.Summary + "\n" + result.RawOutput;
        }
        catch (Exception ex)
        {
            IperfLog = $"iPerf3 failed: {ex.Message}";
        }
        finally
        {
            IsIperf = false;
        }
    }

    [RelayCommand]
    private async Task ToggleIperfServerAsync()
    {
        if (IsIperfServer)
        {
            await _iperf.StopServerAsync();
            IsIperfServer = false;
            ServerButtonLabel = "START SERVER";
            IperfLog = "Server stopped";
            return;
        }

        var progress = new Progress<string>(m => IperfLog = m);
        try
        {
            await _iperf.StartServerAsync(IperfPort, progress);
            IsIperfServer = _iperf.IsServerRunning;
            ServerButtonLabel = IsIperfServer ? "STOP SERVER" : "START SERVER";
        }
        catch (Exception ex)
        {
            IsIperfServer = false;
            ServerButtonLabel = "START SERVER";
            IperfLog = $"Unable to start iPerf3 server: {ex.Message}";
        }
    }
}
