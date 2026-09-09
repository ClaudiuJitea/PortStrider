using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using Material.Icons;

namespace PortStrider.UI.ViewModels;

public sealed class NavItem
{
    public required AppSection Section { get; init; }
    public required string Label { get; init; }
    public required string Caption { get; init; }
    public required MaterialIconKind Icon { get; init; }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private NavItem _selectedNav;
    [ObservableProperty] private ViewModelBase _currentPage;

    public AppSession Session { get; }
    public AutoTestViewModel AutoTest { get; }
    public SwitchViewModel Switch { get; }
    public CaptureViewModel Capture { get; }
    public ToolsViewModel Tools { get; }
    public ReflectorViewModel Reflector { get; }
    public CableViewModel Cable { get; }
    public NeighborsViewModel Neighbors { get; }
    public ReportsViewModel Reports { get; }
    public ProfilesViewModel Profiles { get; }
    public VlanMonitorViewModel VlanMonitor { get; }
    public CapabilitiesViewModel Capabilities { get; }
    public PreFlightViewModel PreFlight { get; }
    public ObservableCollection<NavItem> NavItems { get; } = new();

    public MainViewModel(
        AppSession session,
        AutoTestViewModel autoTest,
        SwitchViewModel @switch,
        CaptureViewModel capture,
        ToolsViewModel tools,
        ReflectorViewModel reflector,
        CableViewModel cable,
        NeighborsViewModel neighbors,
        ReportsViewModel reports,
        ProfilesViewModel profiles,
        VlanMonitorViewModel vlanMonitor,
        CapabilitiesViewModel capabilities,
        PreFlightViewModel preFlight)
    {
        Session = session;
        AutoTest = autoTest;
        Switch = @switch;
        Capture = capture;
        Tools = tools;
        Reflector = reflector;
        Cable = cable;
        Neighbors = neighbors;
        Reports = reports;
        Profiles = profiles;
        VlanMonitor = vlanMonitor;
        Capabilities = capabilities;
        PreFlight = preFlight;

        NavItems.Add(new NavItem { Section = AppSection.AutoTest, Label = "AutoTest", Caption = "Profile-driven", Icon = MaterialIconKind.Speedometer });
        NavItems.Add(new NavItem { Section = AppSection.Switch, Label = "Switch", Caption = "CDP / LLDP / EDP", Icon = MaterialIconKind.LanConnect });
        NavItems.Add(new NavItem { Section = AppSection.Capture, Label = "Capture", Caption = "Stream to PCAP", Icon = MaterialIconKind.Radar });
        NavItems.Add(new NavItem { Section = AppSection.VlanMonitor, Label = "VLAN", Caption = "Top 9 traffic", Icon = MaterialIconKind.ChartPie });
        NavItems.Add(new NavItem { Section = AppSection.Tools, Label = "Tools", Caption = "Ping · iperf3", Icon = MaterialIconKind.HammerWrench });
        NavItems.Add(new NavItem { Section = AppSection.Reflector, Label = "Reflector", Caption = "Loopback peer", Icon = MaterialIconKind.SwapHorizontal });
        NavItems.Add(new NavItem { Section = AppSection.Cable, Label = "Cable", Caption = "TDR / SFP", Icon = MaterialIconKind.EthernetCable });
        NavItems.Add(new NavItem { Section = AppSection.Neighbors, Label = "Neighbors", Caption = "ARP cache", Icon = MaterialIconKind.AccessPointNetwork });
        NavItems.Add(new NavItem { Section = AppSection.Profiles, Label = "Profiles", Caption = "Test settings", Icon = MaterialIconKind.TuneVariant });
        NavItems.Add(new NavItem { Section = AppSection.Reports, Label = "Reports", Caption = "Sessions", Icon = MaterialIconKind.FileDocument });
        NavItems.Add(new NavItem { Section = AppSection.Capabilities, Label = "Capabilities", Caption = "Feature matrix", Icon = MaterialIconKind.CheckDecagram });
        NavItems.Add(new NavItem { Section = AppSection.PreFlight, Label = "Pre-flight", Caption = "Drivers", Icon = MaterialIconKind.ShieldCheck });

        _selectedNav = NavItems[0];
        _currentPage = AutoTest;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) =>
        {
            try { Session.UpdateRates(); }
            catch (Exception ex) { Session.StatusLine = $"Unable to read adapter rates: {ex.Message}"; }
        };
        _timer.Start();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Session.RefreshAdaptersAsync();
            await Task.WhenAll(
                PreFlight.ScanCommand.ExecuteAsync(null),
                Capabilities.RefreshCommand.ExecuteAsync(null));
        }
        catch (Exception ex)
        {
            Session.StatusLine = $"Initialization warning: {ex.Message}";
        }
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value.Section switch
        {
            AppSection.AutoTest => AutoTest,
            AppSection.Switch => Switch,
            AppSection.Capture => Capture,
            AppSection.Tools => Tools,
            AppSection.Reflector => Reflector,
            AppSection.Cable => Cable,
            AppSection.Neighbors => Neighbors,
            AppSection.Reports => Reports,
            AppSection.Profiles => Profiles,
            AppSection.VlanMonitor => VlanMonitor,
            AppSection.Capabilities => Capabilities,
            AppSection.PreFlight => PreFlight,
            _ => AutoTest
        };
        if (value.Section == AppSection.AutoTest) AutoTest.ReloadProfiles();
        if (value.Section == AppSection.Reports) Reports.Reload();
        if (value.Section == AppSection.Profiles) Profiles.Reload();
        if (value.Section == AppSection.Neighbors) _ = Neighbors.RefreshCommand.ExecuteAsync(null);
        if (value.Section == AppSection.Capabilities) _ = Capabilities.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task RefreshAdaptersAsync() => Session.RefreshAdaptersAsync();
}
