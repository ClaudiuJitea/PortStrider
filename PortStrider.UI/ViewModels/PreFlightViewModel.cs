using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class PreFlightViewModel : ViewModelBase
{
    private readonly IPreFlightService _preflight;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private bool _elevated;
    [ObservableProperty] private string _privilegeStatus = "";

    public ObservableCollection<DependencyStatus> Items { get; } = new();
    public bool HasItems => Items.Count > 0;

    public PreFlightViewModel(IPreFlightService preflight)
    {
        _preflight = preflight;
        Elevated = _preflight.HasAdminPrivileges();
        UpdatePrivilegeStatus();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Log = "Checking dependencies…";
        try
        {
            Elevated = _preflight.HasAdminPrivileges();
            UpdatePrivilegeStatus();
            var list = await _preflight.CheckAllAsync();
            Items.Clear();
            foreach (var i in list) Items.Add(i);
            OnPropertyChanged(nameof(HasItems));
            var missing = list.Count(item => !item.IsReady);
            Log = missing == 0
                ? "All detected dependencies are ready."
                : $"{missing} item(s) need attention.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RepairAsync(DependencyStatus? item)
    {
        if (item is null || !item.CanAutoFix) return;
        IsBusy = true;
        var progress = new Progress<string>(m => Log = m);
        try
        {
            await _preflight.RepairAsync(item.Id, progress);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Log = $"Repair failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RepairAllAsync()
    {
        IsBusy = true;
        var progress = new Progress<string>(m => Log = m);
        try
        {
            var itemsToRepair = Items
                .Where(i => i.CanAutoFix && !i.IsReady)
                .OrderBy(i => i.Id == "privs" ? 1 : 0)
                .ToArray();

            foreach (var item in itemsToRepair)
                await _preflight.RepairAsync(item.Id, progress);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Log = $"Repair failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdatePrivilegeStatus()
    {
        PrivilegeStatus = Elevated
            ? "Elevated privileges are active."
            : _preflight.IsWindows
                ? "Standard user mode — click Repair on Capture privileges to relaunch as Administrator."
                : "Standard user mode — your system will request your password when a repair needs administrator access.";
    }
}
