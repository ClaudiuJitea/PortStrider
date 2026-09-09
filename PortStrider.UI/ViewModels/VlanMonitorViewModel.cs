using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class VlanMonitorViewModel : ViewModelBase
{
    private readonly IVlanMonitorService _monitor;
    private readonly AppSession _session;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "Idle";

    public ObservableCollection<VlanObservation> Observations { get; } = new();
    public AppSession Session => _session;
    public bool HasObservations => Observations.Count > 0;

    public VlanMonitorViewModel(IVlanMonitorService monitor, AppSession session)
    {
        _monitor = monitor;
        _session = session;
        _monitor.Updated += (_, rows) => Dispatcher.UIThread.Post(() => Refresh(rows));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_session.SelectedAdapter is null || IsRunning) return;
        try
        {
            await _monitor.StartAsync(_session.SelectedAdapter.CaptureDevice);
            IsRunning = true;
            Status = "Monitoring top VLANs…";
            Refresh(_monitor.Current);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _monitor.StopAsync();
        IsRunning = false;
        Status = "Stopped";
    }

    private void Refresh(IReadOnlyList<VlanObservation> rows)
    {
        Observations.Clear();
        foreach (var row in rows) Observations.Add(row);
        OnPropertyChanged(nameof(HasObservations));
        Status = rows.Count == 0 ? "No VLAN-tagged traffic observed yet." : $"Top {rows.Count} VLAN(s)";
    }
}
