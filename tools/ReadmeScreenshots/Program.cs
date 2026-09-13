using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Infrastructure.Adapters;
using PortStrider.Infrastructure.Cable;
using PortStrider.Infrastructure.Capabilities;
using PortStrider.Infrastructure.PreFlight;
using PortStrider.Infrastructure.Probes;
using PortStrider.Infrastructure.Profiles;
using PortStrider.Infrastructure.Reports;
using PortStrider.Infrastructure.Sniffing;
using PortStrider.Infrastructure.Wifi;
using PortStrider.UI;
using PortStrider.UI.ViewModels;
using PortStrider.UI.Views;

const int width = 1920;
const int height = 1080;
var outputDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "screenshots"));

Directory.CreateDirectory(outputDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var vm = BuildViewModel();
SeedSession(vm.Session);

var window = new MainWindow
{
    DataContext = vm,
    Width = width,
    Height = height
};
window.Show();

Capture(window, vm, outputDir, AppSection.AutoTest, "01-autotest.png", SeedAutoTest);
Capture(window, vm, outputDir, AppSection.Wifi, "02-wifi.png", SeedWifi);
Capture(window, vm, outputDir, AppSection.Cable, "03-cable.png", SeedCable);
Capture(window, vm, outputDir, AppSection.Switch, "04-switch.png", SeedSwitch);

Console.WriteLine($"Saved screenshots to {outputDir}");

static MainViewModel BuildViewModel()
{
    var adapters = new NetworkAdapterService();
    var session = new AppSession(adapters);
    var preflight = new DependencyManager();
    var discovery = new SwitchDiscoveryService();
    var probes = new ProbeService();
    var dhcp = new DhcpTestService();
    var telemetry = new LinkTelemetryService();
    var ifconfig = new InterfaceConfigService();
    var dot1x = new WpaSupplicantDot1xService();
    var autoTest = new AutoTestRunner(probes, dhcp, telemetry, ifconfig, dot1x, adapters);
    var capture = new PacketCaptureService();
    var vlanMonitor = new VlanMonitorService();
    var reflector = new ReflectorService();
    var cable = new CableTestService();
    var sfp = new SfpDiagnosticsService();
    var iperf = new IperfService();
    var neighbors = new NeighborService();
    var reports = new JsonReportStore();
    var profiles = new JsonProfileStore();
    var capabilities = new CapabilityService();

    return new MainViewModel(
        session,
        new AutoTestViewModel(autoTest, discovery, reports, profiles, telemetry, probes, adapters, ifconfig, session),
        new SwitchViewModel(discovery, ifconfig, session),
        new CaptureViewModel(capture, session),
        new ToolsViewModel(probes, iperf, session),
        new ReflectorViewModel(reflector, session),
        new CableViewModel(cable, sfp, session),
        new NeighborsViewModel(neighbors, session),
        new ReportsViewModel(reports),
        new ProfilesViewModel(profiles, session),
        new VlanMonitorViewModel(vlanMonitor, session),
        new CapabilitiesViewModel(capabilities, session),
        new PreFlightViewModel(preflight),
        new WifiViewModel(new WifiService(), session));
}

static void SeedSession(AppSession session)
{
    var ethernet = new AdapterInfo
    {
        Id = "demo-eth0",
        Name = "enp0s31f6",
        Description = "Intel I219-V Gigabit",
        MacAddress = "00:1A:2B:3C:4D:5E",
        Ipv4 = "10.42.7.18",
        Status = OperationalStatusKind.Up,
        SpeedBps = 1_000_000_000,
        Media = LinkMediaKind.Ethernet
    };
    var wifi = new AdapterInfo
    {
        Id = "demo-wlan0",
        Name = "wlp2s0",
        Description = "Intel AX210 WiFi 6E",
        MacAddress = "00:AA:BB:CC:DD:EE",
        Ipv4 = "192.168.4.22",
        Status = OperationalStatusKind.Up,
        SpeedBps = 866_000_000,
        Media = LinkMediaKind.WiFi
    };

    session.Adapters.Clear();
    session.Adapters.Add(ethernet);
    session.Adapters.Add(wifi);
    session.SelectedAdapter = ethernet;
    session.Rates = new InterfaceRates
    {
        RxMbps = 42.6,
        TxMbps = 8.3
    };
    session.StatusLine = "Demo data loaded for documentation screenshots";
}

static void SeedAutoTest(MainViewModel vm)
{
    vm.Session.SelectedAdapter = vm.Session.Adapters.First(a => a.Media == LinkMediaKind.Ethernet);
    vm.AutoTest.Overall = TestStatus.Pass;
    vm.AutoTest.OverallLabel = "PASS";
    vm.AutoTest.DurationLabel = "00:00:18";
    vm.AutoTest.FailCount = 0;
    vm.AutoTest.WarnCount = 1;
    vm.AutoTest.JobLabel = "IDF-12 patch panel";
    vm.AutoTest.SiteLabel = "Building A · Floor 3";
    vm.AutoTest.SwitchInfo = new SwitchInfo
    {
        Protocol = DiscoveryProtocol.Lldp,
        SwitchName = "dist-sw1.lab",
        PortId = "Gi1/0/24",
        VlanId = "120",
        VoiceVlan = "130",
        ManagementIp = "10.0.0.12"
    };
    vm.AutoTest.DiscoveryStatus = "IEEE 802.1AB LLDP";

    vm.AutoTest.Steps.Clear();
    foreach (var (title, status, details) in new (string, TestStatus, string)[]
             {
                 ("Link telemetry", TestStatus.Pass, "1 Gbps · full duplex · MDI-X on"),
                 ("802.1X", TestStatus.Skipped, "Not enabled in profile"),
                 ("DHCP DORA", TestStatus.Pass, "10.42.7.18 / 255.255.255.0 · lease 24h"),
                 ("VLAN tagging", TestStatus.Pass, "VLAN 120 reachable on sub-interface"),
                 ("Gateway reachability", TestStatus.Pass, "10.42.7.1 · 0.8 ms avg"),
                 ("DNS resolution", TestStatus.Warning, "Primary OK · secondary timed out"),
                 ("Target probes", TestStatus.Pass, "3/3 targets responded")
             })
    {
        vm.AutoTest.Steps.Add(new TestStepViewModel
        {
            Title = title,
            Status = status,
            Details = details
        });
    }
}

static void SeedWifi(MainViewModel vm)
{
    vm.Session.SelectedAdapter = vm.Session.Adapters.First(a => a.Media == LinkMediaKind.WiFi);

    var networks = new[]
    {
        new WifiNetwork("Lab-5G", "aa:bb:cc:dd:ee:01", 5180, -48, "WPA2-Enterprise", 80, 5210),
        new WifiNetwork("Guest", "aa:bb:cc:dd:ee:02", 5240, -61, "WPA2", 40, 5230),
        new WifiNetwork("IoT", "aa:bb:cc:dd:ee:03", 5260, -67, "WPA2", 20, 5260),
        new WifiNetwork("Neighbor", "aa:bb:cc:dd:ee:04", 5320, -72, "WPA3", 80, 5290),
        new WifiNetwork("Hidden", "aa:bb:cc:dd:ee:05", 5500, -58, "WPA2", 80, 5530),
        new WifiNetwork("Warehouse", "aa:bb:cc:dd:ee:06", 5745, -64, "WPA2", 40, 5755)
    };

    var history = Enumerable.Range(0, 12)
        .Select(i => new WifiScan(
            DateTimeOffset.Now.AddSeconds(-60 + i * 5),
            networks.Select(n => n with { SignalDbm = n.SignalDbm + Math.Sin(i / 2d) * 3 }).ToArray()))
        .ToArray();

    SetPrivate(vm.Wifi, "_allNetworks", networks);
    var samples = (List<WifiScan>)typeof(WifiViewModel)
        .GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(vm.Wifi)!;
    samples.Clear();
    samples.AddRange(history);
    vm.Wifi.History = history;
    vm.Wifi.SelectedBand = "5 GHz";
    vm.Wifi.LastScan = $"Last scan {DateTimeOffset.Now:HH:mm:ss} · {networks.Length} access points across all bands";
    vm.Wifi.Status = "Scan complete · select an access point to inspect its signal.";
    InvokePrivate(vm.Wifi, "FilterNetworks");
}

static void SeedCable(MainViewModel vm)
{
    vm.Session.SelectedAdapter = vm.Session.Adapters.First(a => a.Media == LinkMediaKind.Ethernet);
    vm.Cable.Result = new CableTestResult
    {
        Supported = true,
        Summary = "Pair C open at 18 m · link up at 1 Gbps",
        Phy = new CablePhyInfo
        {
            LinkDetected = true,
            SpeedMbps = 1000,
            SpeedLabel = "1 Gbps",
            Duplex = "Full",
            AutoNegotiation = "on",
            MdiX = "on (auto)",
            Port = "Twisted Pair",
            LocalMaximum = "1 Gbps",
            PartnerMaximum = "1 Gbps"
        },
        Pairs =
        [
            new CablePairResult { Pair = "Pair A", Status = "OK" },
            new CablePairResult { Pair = "Pair B", Status = "OK" },
            new CablePairResult { Pair = "Pair C", Status = "Open Circuit at 18 m", Distance = "18 m" },
            new CablePairResult { Pair = "Pair D", Status = "OK" }
        ],
        RawOutput = "TDR pair results captured from ethtool (demo)."
    };
    vm.Cable.ProgressText = vm.Cable.Result.Summary;
}

static void SeedSwitch(MainViewModel vm)
{
    vm.Session.SelectedAdapter = vm.Session.Adapters.First(a => a.Media == LinkMediaKind.Ethernet);
    vm.Switch.Info = new SwitchInfo
    {
        Protocol = DiscoveryProtocol.Lldp,
        SwitchName = "dist-sw1.lab",
        PortId = "Gi1/0/24",
        PortDescription = "IDF-12 patch · AP uplink",
        VlanId = "120",
        NativeVlan = "1",
        VoiceVlan = "130",
        ManagementIp = "10.0.0.12",
        Model = "C9300-48P",
        ChassisId = "f0:9e:63:12:34:56",
        Capabilities = "Bridge, Router",
        Poe = "802.3at · 30W available",
        Duplex = "Full",
        SoftwareVersion = "17.9.4a",
        ObservedVlans = [1, 120, 130, 200],
        Details =
        [
            "System name: dist-sw1.lab",
            "Port description: IDF-12 patch · AP uplink",
            "Management address: 10.0.0.12",
            "LLDP-MED network policy: voice VLAN 130"
        ]
    };
    vm.Switch.Status = "IEEE 802.1AB LLDP";
}

static void Capture(MainWindow window, MainViewModel vm, string outputDir, AppSection section, string fileName, Action<MainViewModel> seed)
{
    seed(vm);
    vm.SelectedNav = vm.NavItems.First(n => n.Section == section);
    Render(window);
    var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException($"Nothing rendered for {fileName}");
    var path = Path.Combine(outputDir, fileName);
    frame.Save(path);
    Console.WriteLine(path);
}

static void Render(MainWindow window)
{
    window.Show();
    Dispatcher.UIThread.RunJobs();
    for (var i = 0; i < 8; i++)
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

static void SetPrivate(object target, string field, object value) =>
    target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

static void InvokePrivate(object target, string method) =>
    target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
