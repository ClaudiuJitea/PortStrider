using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class AppSession : ObservableObject
{
    private readonly IAdapterService _adapters;
    private InterfaceSnapshot? _lastSnap;
    private DateTimeOffset _lastStamp;

    [ObservableProperty] private AdapterInfo? _selectedAdapter;
    [ObservableProperty] private InterfaceRates? _rates;
    [ObservableProperty] private string _statusLine = "Select an adapter";
    [ObservableProperty] private bool _isScanning;

    public ObservableCollection<AdapterInfo> Adapters { get; } = new();
    public bool HasAdapter => SelectedAdapter is not null;

    public AppSession(IAdapterService adapters)
    {
        _adapters = adapters;
    }

    public async Task RefreshAdaptersAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusLine = "Scanning network adapters…";
        IReadOnlyList<AdapterInfo> list;
        try
        {
            list = await Task.Run(() => _adapters.ListAdapters(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Adapter scan cancelled";
            return;
        }
        catch (Exception ex)
        {
            StatusLine = $"Adapter scan failed: {ex.Message}";
            return;
        }
        finally
        {
            IsScanning = false;
        }

        var selectedId = SelectedAdapter?.Id;
        Adapters.Clear();
        foreach (var a in list) Adapters.Add(a);
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == selectedId)
                          ?? Adapters.FirstOrDefault(a => a.IsUp)
                          ?? Adapters.FirstOrDefault();
        UpdateRates();
    }

    public void UpdateRates()
    {
        if (SelectedAdapter is null)
        {
            Rates = null;
            StatusLine = "No adapter selected";
            return;
        }

        var snap = _adapters.Snapshot(SelectedAdapter.Id);
        if (snap is not null && _lastSnap is not null && _lastSnap.AdapterId == snap.AdapterId)
        {
            var dt = (snap.Timestamp - _lastStamp).TotalSeconds;
            if (dt > 0.2)
            {
                Rates = new InterfaceRates
                {
                    RxMbps = (snap.RxBytes - _lastSnap.RxBytes) * 8d / dt / 1_000_000d,
                    TxMbps = (snap.TxBytes - _lastSnap.TxBytes) * 8d / dt / 1_000_000d,
                    RxErrors = snap.RxErrors,
                    TxErrors = snap.TxErrors,
                    RxDropped = snap.RxDropped,
                    TxDropped = snap.TxDropped
                };
            }
        }

        _lastSnap = snap;
        _lastStamp = snap?.Timestamp ?? DateTimeOffset.Now;

        var ip = SelectedAdapter.Ipv4 ?? "no IPv4";
        var link = SelectedAdapter.IsUp ? SelectedAdapter.SpeedLabel : "down";
        StatusLine = $"{SelectedAdapter.Name}  ·  {link}  ·  {ip}  ·  {SelectedAdapter.MacAddress}";
    }

    partial void OnSelectedAdapterChanged(AdapterInfo? value)
    {
        _lastSnap = null;
        OnPropertyChanged(nameof(HasAdapter));
        UpdateRates();
    }
}
