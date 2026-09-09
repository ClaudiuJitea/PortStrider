using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class SwitchViewModel : ViewModelBase
{
    private readonly IDiscoveryService _discovery;
    private readonly IInterfaceConfigService _ifconfig;
    private readonly AppSession _session;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _flashCts;

    [ObservableProperty] private SwitchInfo _info = SwitchInfo.Idle();
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private int _timeoutSeconds = 35;
    [ObservableProperty] private string _status = "Idle — start a listen on the selected NIC";

    [ObservableProperty] private bool _isFlashing;
    /// <summary>Seconds per blink cycle. G2 exposes a Slow…Fast slider; auto-negotiation puts a floor around 1.5 s.</summary>
    [ObservableProperty] private double _flashPeriodSeconds = 4;
    [ObservableProperty] private string _flashStatus = "";
    [ObservableProperty] private string _flashButtonLabel = "FLASH PORT";

    public AppSession Session => _session;
    public bool FlashSupported => _ifconfig.IsSupported;

    public SwitchViewModel(IDiscoveryService discovery, IInterfaceConfigService ifconfig, AppSession session)
    {
        _discovery = discovery;
        _ifconfig = ifconfig;
        _session = session;
    }

    [RelayCommand]
    private async Task ListenAsync()
    {
        if (_session.SelectedAdapter is null || IsListening) return;
        IsListening = true;
        Status = $"Listening up to {TimeoutSeconds}s for LLDP / CDP / EDP…";
        Info = SwitchInfo.Idle();
        _cts = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() =>
                _discovery.SniffSwitchPortAsync(_session.SelectedAdapter.CaptureDevice, TimeoutSeconds, false, _cts.Token));
            Info = result;
            Status = result.ProtocolLabel;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsListening = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>
    /// G2 "Flash Port": makes the switch-port LED blink by cycling the link so the port can be found in the closet.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFlashAsync()
    {
        if (IsFlashing)
        {
            _flashCts?.Cancel();
            return;
        }

        if (_session.SelectedAdapter is null) return;
        if (!_ifconfig.IsSupported)
        {
            FlashStatus = "Flash Port needs Linux with iproute2.";
            return;
        }

        IsFlashing = true;
        FlashButtonLabel = "STOP FLASHING";
        FlashStatus = "Cycling link… the switch LED for this port blinks at the same rhythm.";
        _flashCts = new CancellationTokenSource();
        var progress = new Progress<string>(m => FlashStatus = m);
        try
        {
            await _ifconfig.FlashPortAsync(_session.SelectedAdapter.Name, TimeSpan.FromSeconds(Math.Max(1.5, FlashPeriodSeconds)), progress, _flashCts.Token);
            FlashStatus = "Stopped — link restored.";
        }
        catch (Exception ex)
        {
            FlashStatus = ex.Message;
        }
        finally
        {
            IsFlashing = false;
            FlashButtonLabel = "FLASH PORT";
            _flashCts?.Dispose();
            _flashCts = null;
            _ = _session.RefreshAdaptersAsync();
        }
    }
}
