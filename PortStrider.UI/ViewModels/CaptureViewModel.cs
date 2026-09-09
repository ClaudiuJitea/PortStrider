using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Sniffing;

namespace PortStrider.UI.ViewModels;

public partial class CaptureViewModel : ViewModelBase
{
    private readonly PacketCaptureService _capture;
    private readonly AppSession _session;
    private readonly ConcurrentQueue<CapturedFrame> _pending = new();
    private readonly DispatcherTimer _flush;
    private const int MaxRows = 2500;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private CapturedFrame? _selectedFrame;
    [ObservableProperty] private int _droppedUi;
    [ObservableProperty] private int _snapLength;
    [ObservableProperty] private string _preset = "All traffic";

    public ObservableCollection<CapturedFrame> Frames { get; } = new();
    public AppSession Session => _session;
    public bool HasFrames => Frames.Count > 0;

    public CaptureViewModel(ICaptureService capture, AppSession session)
    {
        _capture = (PacketCaptureService)capture;
        _session = session;
        _capture.FrameArrived += (_, frame) => _pending.Enqueue(frame);
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_session.SelectedAdapter is null || IsCapturing) return;
        Frames.Clear();
        OnPropertyChanged(nameof(HasFrames));
        DroppedUi = 0;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortStrider");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
        try
        {
            await _capture.StartAsync(_session.SelectedAdapter.CaptureDevice, new CaptureOptions
            {
                BpfFilter = string.IsNullOrWhiteSpace(Filter) ? null : Filter,
                OutputPath = path,
                SnapLength = SnapLength,
                MaxBytes = 2L * 1024 * 1024 * 1024,
                AllowJumboFrames = true
            });
            IsCapturing = true;
            Status = $"Capturing on {_session.SelectedAdapter.Name} → {path}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _capture.StopAsync();
        IsCapturing = false;
        var stats = _capture.Statistics;
        Status = $"Stopped · {stats.FramesCaptured:N0} frames · {stats.BytesCaptured:N0} bytes · dropped {stats.FramesDropped}";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Frames.Count == 0) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortStrider");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"capture-sample-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
        try
        {
            await _capture.SavePcapAsync(path, Frames.ToArray());
            Status = $"Wrote sample {path}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void ApplyPreset(string preset)
    {
        Preset = preset;
        Filter = preset switch
        {
            "LLDP/CDP" => "ether proto 0x88cc or ether dst 01:00:0c:cc:cc:cc or ether proto 0x88b6",
            "IPv4" => "ip",
            "TCP" => "tcp",
            "UDP" => "udp",
            _ => ""
        };
    }

    [RelayCommand]
    private void Clear()
    {
        Frames.Clear();
        SelectedFrame = null;
        OnPropertyChanged(nameof(HasFrames));
    }

    private void Flush()
    {
        var n = 0;
        var trimmed = 0;
        while (n < 200 && _pending.TryDequeue(out var frame))
        {
            Frames.Add(frame);
            n++;
            while (Frames.Count > MaxRows)
            {
                Frames.RemoveAt(0);
                trimmed++;
            }
        }

        if (n > 0) OnPropertyChanged(nameof(HasFrames));

        if (trimmed > 0)
        {
            DroppedUi += trimmed;
            _capture.NotifyUiTrimmed(trimmed);
        }

        if (IsCapturing)
        {
            var stats = _capture.Statistics;
            Status = $"{stats.FramesCaptured:N0} captured · UI trimmed {DroppedUi} · capture dropped {stats.FramesDropped}";
        }
    }
}
