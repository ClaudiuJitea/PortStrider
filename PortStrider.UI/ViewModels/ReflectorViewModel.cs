using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class ReflectorViewModel : ViewModelBase
{
    private readonly IReflectorService _reflector;
    private readonly AppSession _session;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private ReflectorSwapMode _swapMode = ReflectorSwapMode.MacAndIp;
    [ObservableProperty] private ReflectorFilterMode _filterMode = ReflectorFilterMode.OwnMacAndNetAlly;
    [ObservableProperty] private string _status = "Idle — start the reflector, then point iperf3 -u / a NetAlly-style peer at this NIC.";
    [ObservableProperty] private string _buttonLabel = "START REFLECTOR";
    [ObservableProperty] private long _framesReceived;
    [ObservableProperty] private long _framesReflected;
    [ObservableProperty] private long _framesFiltered;
    [ObservableProperty] private string _bytesReceivedLabel = "0 B";
    [ObservableProperty] private string _bytesReflectedLabel = "0 B";
    [ObservableProperty] private string _rateLabel = "0.00 Mbps";
    [ObservableProperty] private string _durationLabel = "00:00";

    private long _lastBytes;
    private DateTimeOffset _lastTick;

    public IReadOnlyList<ReflectorSwapMode> SwapModes { get; } = Enum.GetValues<ReflectorSwapMode>();
    public IReadOnlyList<ReflectorFilterMode> FilterModes { get; } = Enum.GetValues<ReflectorFilterMode>();
    public AppSession Session => _session;

    public ReflectorViewModel(IReflectorService reflector, AppSession session)
    {
        _reflector = reflector;
        _session = session;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh(_reflector.Statistics);
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRunning)
        {
            await _reflector.StopAsync();
            _timer.Stop();
            IsRunning = false;
            ButtonLabel = "START REFLECTOR";
            Refresh(_reflector.Statistics);
            Status = "Reflector stopped.";
            return;
        }

        if (_session.SelectedAdapter is null) return;
        try
        {
            await _reflector.StartAsync(_session.SelectedAdapter.CaptureDevice, new ReflectorOptions
            {
                Swap = SwapMode,
                Filter = FilterMode,
                OwnMac = _session.SelectedAdapter.MacAddress
            });
            IsRunning = true;
            ButtonLabel = "STOP REFLECTOR";
            _lastBytes = 0;
            _lastTick = DateTimeOffset.Now;
            _timer.Start();
            Status = $"Reflecting on {_session.SelectedAdapter.Name} ({_session.SelectedAdapter.MacAddress}, {_session.SelectedAdapter.Ipv4 ?? "no IPv4"}) — "
                     + (FilterMode == ReflectorFilterMode.All
                         ? "WARNING: reflecting all traffic on this segment."
                         : "frames addressed to this MAC are swapped and sent back.");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void Refresh(ReflectorStatistics stats)
    {
        FramesReceived = stats.FramesReceived;
        FramesReflected = stats.FramesReflected;
        FramesFiltered = stats.FramesFiltered;
        BytesReceivedLabel = Bytes(stats.BytesReceived);
        BytesReflectedLabel = Bytes(stats.BytesReflected);
        DurationLabel = stats.Duration.ToString(@"mm\:ss");

        var now = DateTimeOffset.Now;
        var dt = (now - _lastTick).TotalSeconds;
        if (dt >= 0.4)
        {
            var mbps = (stats.BytesReflected - _lastBytes) * 8d / dt / 1_000_000d;
            RateLabel = $"{Math.Max(0, mbps):0.00} Mbps";
            _lastBytes = stats.BytesReflected;
            _lastTick = now;
        }
    }

    private static string Bytes(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00} GB",
        >= 1_000_000 => $"{value / 1_000_000d:0.00} MB",
        >= 1_000 => $"{value / 1_000d:0.0} kB",
        _ => $"{value} B"
    };
}
