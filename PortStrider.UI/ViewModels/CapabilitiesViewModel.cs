using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class CapabilitiesViewModel : ViewModelBase
{
    private readonly ICapabilityService _capabilities;
    private readonly AppSession _session;

    [ObservableProperty] private string _status = "Select an adapter to inspect capabilities.";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<FeatureCapability> Features { get; } = new();
    public AppSession Session => _session;
    public bool HasFeatures => Features.Count > 0;

    public CapabilitiesViewModel(ICapabilityService capabilities, AppSession session)
    {
        _capabilities = capabilities;
        _session = session;
        _session.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(AppSession.SelectedAdapter))
                await RefreshAsync();
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        if (_session.SelectedAdapter is null)
        {
            Features.Clear();
            OnPropertyChanged(nameof(HasFeatures));
            Status = "No adapter selected.";
            return;
        }

        IsBusy = true;
        Status = "Evaluating adapter capabilities…";
        try
        {
            var result = await _capabilities.EvaluateAsync(_session.SelectedAdapter);
            Features.Clear();
            foreach (var feature in result.Features) Features.Add(feature);
            OnPropertyChanged(nameof(HasFeatures));
            Status = $"{result.AdapterName}: {result.Features.Count(f => f.Level == Core.Enums.CapabilityLevel.Supported)} supported, " +
                     $"{result.Features.Count(f => f.Level == Core.Enums.CapabilityLevel.Degraded)} degraded, " +
                     $"{result.Features.Count(f => f.Level == Core.Enums.CapabilityLevel.Unavailable)} unavailable";
        }
        catch (Exception ex)
        {
            Features.Clear();
            OnPropertyChanged(nameof(HasFeatures));
            Status = $"Capability check failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
