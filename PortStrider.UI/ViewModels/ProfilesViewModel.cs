using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public sealed record StopAfterOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly AppSession _session;

    [ObservableProperty] private TestProfile? _selected;
    [ObservableProperty] private string _status = "Select or edit a profile.";
    [ObservableProperty] private string _newProfileName = "Site profile";
    [ObservableProperty] private bool _enableIpv6 = true;
    [ObservableProperty] private bool _enablePoeAdvertised = true;
    [ObservableProperty] private AddressMode _addressMode = AddressMode.Dhcp;
    [ObservableProperty] private DhcpOptionKind _dhcpOption = DhcpOptionKind.None;
    [ObservableProperty] private string _dhcpVendorClass = "";
    [ObservableProperty] private string _proxyUrl = "";
    [ObservableProperty] private int? _vlanId;
    [ObservableProperty] private int _vlanPriority;
    [ObservableProperty] private CableUnit _cableUnit = CableUnit.Meters;

    // Connect settings (G2)
    [ObservableProperty] private LinkSpeedSetting _linkSpeed = LinkSpeedSetting.Auto;
    [ObservableProperty] private DuplexSetting _linkDuplex = DuplexSetting.Auto;
    [ObservableProperty] private string _overrideMac = "";
    [ObservableProperty] private string _staticIpv4 = "";
    [ObservableProperty] private string _staticSubnet = "";
    [ObservableProperty] private string _staticGateway = "";
    [ObservableProperty] private string _staticDns = "";

    // 802.1X
    [ObservableProperty] private bool _enable8021X;
    [ObservableProperty] private EapType _eapType = EapType.Peap;
    [ObservableProperty] private string _username8021X = "";
    [ObservableProperty] private string _password8021X = "";
    [ObservableProperty] private string _anonymousIdentity8021X = "";
    [ObservableProperty] private string _caCertPath8021X = "";
    [ObservableProperty] private string _clientCertPath8021X = "";
    [ObservableProperty] private string _clientKeyPath8021X = "";

    // Test flow
    [ObservableProperty] private StopAfterOption? _stopAfter;
    [ObservableProperty] private int _pingCount = 3;
    [ObservableProperty] private string _dnsQueryName = "cloudflare.com";

    public ObservableCollection<TestProfile> Profiles { get; } = new();
    public ObservableCollection<StopAfterOption> StopAfterOptions { get; } = new();
    public IReadOnlyList<AddressMode> AddressModes { get; } = Enum.GetValues<AddressMode>();
    public IReadOnlyList<DhcpOptionKind> DhcpOptions { get; } = Enum.GetValues<DhcpOptionKind>();
    public IReadOnlyList<CableUnit> CableUnits { get; } = Enum.GetValues<CableUnit>();
    public IReadOnlyList<LinkSpeedSetting> LinkSpeeds { get; } = Enum.GetValues<LinkSpeedSetting>();
    public IReadOnlyList<DuplexSetting> DuplexSettings { get; } = Enum.GetValues<DuplexSetting>();
    public IReadOnlyList<EapType> EapTypes { get; } = Enum.GetValues<EapType>();
    public IReadOnlyList<int> VlanPriorities { get; } = Enumerable.Range(0, 8).ToArray();

    public bool IsStatic => AddressMode == AddressMode.Static;
    public bool IsEapTls => EapType == EapType.Tls;

    partial void OnAddressModeChanged(AddressMode value) => OnPropertyChanged(nameof(IsStatic));
    partial void OnEapTypeChanged(EapType value) => OnPropertyChanged(nameof(IsEapTls));

    public ProfilesViewModel(IProfileStore store, AppSession session)
    {
        _store = store;
        _session = session;
        Reload();
    }

    public AppSession Session => _session;

    public void Reload()
    {
        var selectedId = Selected?.Id;
        Profiles.Clear();
        foreach (var profile in _store.List()) Profiles.Add(profile);
        Selected = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Selected ?? _store.GetDefault();
    }

    partial void OnSelectedChanged(TestProfile? value)
    {
        if (value is null) return;
        EnableIpv6 = value.EnableIpv6;
        EnablePoeAdvertised = value.EnablePoeAdvertised;
        AddressMode = value.AddressMode;
        DhcpOption = value.DhcpOption;
        DhcpVendorClass = value.DhcpVendorClass ?? "";
        ProxyUrl = value.ProxyUrl ?? "";
        VlanId = value.VlanId;
        VlanPriority = Math.Clamp(value.VlanPriority, 0, 7);
        CableUnit = value.CableUnit;
        LinkSpeed = value.LinkSpeed;
        LinkDuplex = value.LinkDuplex;
        OverrideMac = value.OverrideMac ?? "";
        StaticIpv4 = value.StaticIpv4 ?? "";
        StaticSubnet = value.StaticSubnet ?? "";
        StaticGateway = value.StaticGateway ?? "";
        StaticDns = value.StaticDns ?? "";
        Enable8021X = value.Enable8021X;
        EapType = value.EapType;
        Username8021X = value.Username8021X ?? "";
        Password8021X = value.Password8021X ?? "";
        AnonymousIdentity8021X = value.AnonymousIdentity8021X ?? "";
        CaCertPath8021X = value.CaCertPath8021X ?? "";
        ClientCertPath8021X = value.ClientCertPath8021X ?? "";
        ClientKeyPath8021X = value.ClientKeyPath8021X ?? "";
        PingCount = value.PingCount <= 0 ? 3 : value.PingCount;
        DnsQueryName = string.IsNullOrWhiteSpace(value.DnsQueryName) ? "cloudflare.com" : value.DnsQueryName;
        RebuildStopAfter(value);
        Status = $"Loaded {value.Name}";
    }

    private void RebuildStopAfter(TestProfile profile)
    {
        StopAfterOptions.Clear();
        StopAfterOptions.Add(new StopAfterOption("", "Run everything"));
        StopAfterOptions.Add(new StopAfterOption("link", "Stop after Link"));
        StopAfterOptions.Add(new StopAfterOption("8021x", "Stop after 802.1X"));
        StopAfterOptions.Add(new StopAfterOption("dhcp", "Stop after DHCP / addressing"));
        StopAfterOptions.Add(new StopAfterOption("vlan", "Stop after VLAN"));
        StopAfterOptions.Add(new StopAfterOption("gw", "Stop after Gateway"));
        StopAfterOptions.Add(new StopAfterOption("dns", "Stop after DNS"));
        foreach (var target in profile.Targets)
            StopAfterOptions.Add(new StopAfterOption($"target-{target.Id}", $"Stop after {target.Label}"));
        StopAfter = StopAfterOptions.FirstOrDefault(o => o.Id == profile.StopAfterStepId || o.Id == $"target-{profile.StopAfterStepId}")
                    ?? StopAfterOptions[0];
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is null) return;
        var updated = Selected with
        {
            AdapterId = _session.SelectedAdapter?.Id,
            EnableIpv6 = EnableIpv6,
            EnablePoeAdvertised = EnablePoeAdvertised,
            AddressMode = AddressMode,
            DhcpOption = DhcpOption,
            DhcpVendorClass = DhcpVendorClass,
            ProxyUrl = ProxyUrl,
            VlanId = VlanId is > 0 ? VlanId : null,
            VlanPriority = VlanPriority,
            CableUnit = CableUnit,
            LinkSpeed = LinkSpeed,
            LinkDuplex = LinkDuplex,
            OverrideMac = NullIfEmpty(OverrideMac),
            StaticIpv4 = NullIfEmpty(StaticIpv4),
            StaticSubnet = NullIfEmpty(StaticSubnet),
            StaticGateway = NullIfEmpty(StaticGateway),
            StaticDns = NullIfEmpty(StaticDns),
            Enable8021X = Enable8021X,
            EapType = EapType,
            Username8021X = NullIfEmpty(Username8021X),
            Password8021X = string.IsNullOrEmpty(Password8021X) ? null : Password8021X,
            AnonymousIdentity8021X = NullIfEmpty(AnonymousIdentity8021X),
            CaCertPath8021X = NullIfEmpty(CaCertPath8021X),
            ClientCertPath8021X = NullIfEmpty(ClientCertPath8021X),
            ClientKeyPath8021X = NullIfEmpty(ClientKeyPath8021X),
            StopAfterStepId = StopAfter?.Id ?? "",
            PingCount = Math.Clamp(PingCount, 1, 10),
            DnsQueryName = string.IsNullOrWhiteSpace(DnsQueryName) ? "cloudflare.com" : DnsQueryName.Trim()
        };
        _store.Save(updated);
        Reload();
        Selected = _store.Get(updated.Id);
        Status = "Profile saved.";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var copy = _store.Duplicate(Selected.Id, $"{Selected.Name} copy");
        Reload();
        Selected = copy;
        Status = $"Duplicated as {copy.Name}.";
    }

    [RelayCommand]
    private void Create()
    {
        var profile = TestProfile.Default() with
        {
            Id = Guid.NewGuid(),
            Name = NewProfileName,
            AdapterId = _session.SelectedAdapter?.Id
        };
        _store.Save(profile);
        Reload();
        Selected = profile;
        Status = "Profile created.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Selected is null) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortStrider");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"profile-{Selected.Name.Replace(' ', '-')}.json");
        await _store.ExportAsync(Selected, path);
        Status = $"Exported {path}";
    }
}
