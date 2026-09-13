using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class CableViewModel : ViewModelBase
{
    private readonly ICableTestService _cable;
    private readonly ISfpDiagnosticsService _sfp;
    private readonly AppSession _session;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private CableTestResult? _result;
    [ObservableProperty] private SfpDiagnostics? _sfpResult;
    [ObservableProperty] private string _progressText = "TDR uses the NIC PHY. Consumer USB adapters often cannot run it.";
    [ObservableProperty] private string _cableId = "";
    [ObservableProperty] private CableUnit _unit = CableUnit.Meters;

    public FeatureCapability Wiremap => _cable.WiremapCapability();
    public FeatureCapability Tone => _cable.ToneCapability();
    public IReadOnlyList<CableUnit> Units { get; } = Enum.GetValues<CableUnit>();
    public AppSession Session => _session;
    public bool HasResult => Result is not null;
    public bool HasSfpResult => SfpResult is not null;
    public bool HasPairs => Result?.HasPairs is true;
    public bool HasUnsupportedResult => Result is { Supported: false };
    public bool LinkUp => Result?.LinkUp is true;

    partial void OnResultChanged(CableTestResult? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasPairs));
        OnPropertyChanged(nameof(HasUnsupportedResult));
        OnPropertyChanged(nameof(LinkUp));
    }
    partial void OnSfpResultChanged(SfpDiagnostics? value) => OnPropertyChanged(nameof(HasSfpResult));

    public CableViewModel(ICableTestService cable, ISfpDiagnosticsService sfp, AppSession session)
    {
        _cable = cable;
        _sfp = sfp;
        _session = session;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (_session.SelectedAdapter is null) return;
        IsRunning = true;
        var progress = new Progress<string>(m => ProgressText = m);
        try
        {
            Result = await _cable.RunAsync(_session.SelectedAdapter.Name, Unit, progress);
            ProgressText = Result.Summary;
            SfpResult = await _sfp.ReadAsync(_session.SelectedAdapter.Name);
        }
        catch (Exception ex)
        {
            ProgressText = $"Cable diagnostics failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
