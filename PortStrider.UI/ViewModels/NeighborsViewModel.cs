using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class NeighborsViewModel : ViewModelBase
{
    private readonly INeighborService _neighbors;
    private readonly AppSession _session;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "Load the ARP / neighbor cache for this host";

    public ObservableCollection<NeighborEntry> Entries { get; } = new();
    public AppSession Session => _session;
    public bool HasEntries => Entries.Count > 0;

    public NeighborsViewModel(INeighborService neighbors, AppSession session)
    {
        _neighbors = neighbors;
        _session = session;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _neighbors.ListAsync(_session.SelectedAdapter?.Name);
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            OnPropertyChanged(nameof(HasEntries));
            Status = Entries.Count == 0
                ? "No neighbors found for the selected interface."
                : $"{Entries.Count} neighbors";
        }
        catch (Exception ex)
        {
            Entries.Clear();
            OnPropertyChanged(nameof(HasEntries));
            Status = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
