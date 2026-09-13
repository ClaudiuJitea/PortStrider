using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class WifiViewModel : ViewModelBase
{
    private readonly IWifiService _wifi;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<WifiNetwork> _allNetworks = [];
    private readonly List<WifiScan> _samples = [];
    private WifiNetwork? _trackedNetwork;
    private bool _updatingNetworks;

    public AppSession Session { get; }
    public IReadOnlyList<string> Bands { get; } = ["2.4 GHz", "5 GHz", "6 GHz"];
    [ObservableProperty] private string _selectedBand = "2.4 GHz";
    [ObservableProperty] private IReadOnlyList<WifiNetwork> _networks = [];
    [ObservableProperty] private IReadOnlyList<WifiScan> _history = [];
    [ObservableProperty] private WifiNetwork? _selectedNetwork;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "Select a WiFi adapter, then scan to see nearby access points.";
    [ObservableProperty] private string _lastScan = "No scans yet";
    public bool IsWifiAdapter => Session.SelectedAdapter?.Media == LinkMediaKind.WiFi;
    public bool HasNetworks => Networks.Count > 0;
    public string StrongestSignal => Networks.Count > 0 ? Networks.Max(n => n.SignalDbm).ToString("0") + " dBm" : "—";
    public int ChannelCount => Networks.Select(n => n.Channel).Distinct().Count();
    public string StrongestName => Networks.FirstOrDefault()?.Name ?? "No networks observed";
    public WifiNetwork? InspectedNetwork => _trackedNetwork;
    public bool HasSelection => _trackedNetwork is not null;
    public string SignalRank => _trackedNetwork is { } n && Networks.Any(a => a.Key == n.Key)
        ? $"#{1 + Networks.Count(a => a.SignalDbm > n.SignalDbm)} of {Networks.Count} by signal in {SelectedBand}"
        : "Not seen in latest scan";
    public string OverlapSummary => _trackedNetwork is { } n
        ? $"{Networks.Count(a => WifiAnalysis.Overlaps(n, a))} overlapping APs · {Networks.Count(a => a.Key != n.Key && a.Channel == n.Channel)} on the same primary channel"
        : "Select an access point";
    public IReadOnlyList<WifiNetwork> OverlappingNetworks => _trackedNetwork is { } n
        ? Networks.Where(a => WifiAnalysis.Overlaps(n, a)).ToArray() : [];
    public string SignalRange => _trackedNetwork is { } n && _samples.SelectMany(s => s.Networks).Where(a => a.Key == n.Key).ToArray() is { Length: > 0 } readings
        ? $"{readings.Min(a => a.SignalDbm):0} to {readings.Max(a => a.SignalDbm):0} dBm · average {readings.Average(a => a.SignalDbm):0.0} dBm"
        : "No history yet";
    public string LastSeen => _samples.LastOrDefault(s => s.Networks.Any(n => n.Key == SelectedKey))?.Timestamp.ToString("HH:mm:ss") ?? "—";
    public string Footprint => _trackedNetwork is { } n
        ? $"{n.PlotCenterMhz - n.PlotWidthMhz / 2d:0}–{n.PlotCenterMhz + n.PlotWidthMhz / 2d:0} MHz" + (n.WidthMhz is null ? " (20 MHz guide; width unknown)" : "")
        : "—";
    public string SelectedKey => _trackedNetwork?.Key ?? "";
    public string HistoryTitle => _trackedNetwork is { } n
        ? $"{n.Name} · {n.Bssid}" + (Networks.Any(current => current.Key == n.Key) ? "" : " · not seen in latest scan")
        : "Select an access point to follow its signal";

    public WifiViewModel(IWifiService wifi, AppSession session)
    {
        _wifi = wifi;
        Session = session;
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppSession.SelectedAdapter)) return;
            Stop();
            _allNetworks = [];
            _trackedNetwork = null;
            _samples.Clear();
            History = [];
            LastScan = "No scans yet";
            FilterNetworks();
            Status = IsWifiAdapter ? "Ready to scan the selected WiFi adapter." : "Select a WiFi adapter in the top toolbar. Ethernet cannot scan WiFi.";
            OnPropertyChanged(nameof(IsWifiAdapter));
            NotifyCommands();
        };
    }

    private bool CanScan() => !IsRunning && IsWifiAdapter;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunAsync(false);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task StartAsync() => RunAsync(true);

    [RelayCommand]
    public void Stop() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void SelectStrongest() => SelectedNetwork = Networks.FirstOrDefault();

    private async Task RunAsync(bool live)
    {
        if (!CanScan() || Session.SelectedAdapter is not { } adapter) return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsRunning = true;
        try
        {
            do
            {
                Status = $"Scanning {adapter.Name}…";
                var result = await _wifi.ScanAsync(adapter, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _allNetworks = result.Networks;
                _samples.Add(result);
                if (_samples.Count > 60) _samples.RemoveAt(0);
                History = _samples.ToArray();
                FilterNetworks();
                LastScan = $"Last scan {result.Timestamp:HH:mm:ss} · {result.Networks.Count} access points across all bands";
                Status = result.Networks.Count == 0 ? "No access points found. Check range, radio state, and adapter band support."
                    : live ? "Live · next scan in 5 seconds" : "Scan complete · select an access point to inspect its signal.";
                if (live) await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
            } while (live);
        }
        catch (OperationCanceledException)
        {
            if (Session.SelectedAdapter == adapter) Status = "Stopped · last completed scan retained.";
        }
        catch (Exception ex)
        {
            if (Session.SelectedAdapter == adapter) Status = $"Scan failed: {ex.Message} Last completed scan retained.";
        }
        finally
        {
            _scanCancellation = null;
            IsRunning = false;
        }
    }

    private void FilterNetworks()
    {
        var tracked = _trackedNetwork;
        var next = _allNetworks.Where(n => n.Band == SelectedBand).OrderByDescending(n => n.SignalDbm).ToArray();
        var selection = tracked is null ? next.FirstOrDefault() : next.FirstOrDefault(n => n.Key == tracked.Key);
        // Bound selectors can change selection while their items are replaced. Keep the tracked BSSID stable.
        _updatingNetworks = true;
        try
        {
            Networks = next;
            SelectedNetwork = selection;
            _trackedNetwork = selection ?? tracked;
        }
        finally { _updatingNetworks = false; }
        OnPropertyChanged(nameof(HasNetworks));
        OnPropertyChanged(nameof(StrongestSignal));
        OnPropertyChanged(nameof(ChannelCount));
        OnPropertyChanged(nameof(StrongestName));
        OnPropertyChanged(nameof(SelectedKey));
        OnPropertyChanged(nameof(HistoryTitle));
        NotifyInspection();
    }

    partial void OnSelectedBandChanged(string value)
    {
        _trackedNetwork = null;
        FilterNetworks();
    }
    partial void OnSelectedNetworkChanged(WifiNetwork? value)
    {
        if (_updatingNetworks) return;
        if (value is not null) _trackedNetwork = value;
        OnPropertyChanged(nameof(SelectedKey));
        OnPropertyChanged(nameof(HistoryTitle));
        NotifyInspection();
    }
    private void NotifyInspection()
    {
        OnPropertyChanged(nameof(InspectedNetwork));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SignalRank));
        OnPropertyChanged(nameof(OverlapSummary));
        OnPropertyChanged(nameof(OverlappingNetworks));
        OnPropertyChanged(nameof(SignalRange));
        OnPropertyChanged(nameof(LastSeen));
        OnPropertyChanged(nameof(Footprint));
    }
    partial void OnIsRunningChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
}
