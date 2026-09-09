using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.UI.ViewModels;

public partial class ReportsViewModel : ViewModelBase
{
    private readonly IReportStore _store;

    [ObservableProperty] private SessionReport? _selected;
    [ObservableProperty] private string _preview = "Run an AutoTest to capture a session.";
    [ObservableProperty] private string _exportStatus = "";
    [ObservableProperty] private string _search = "";

    public ObservableCollection<SessionReport> Sessions { get; } = new();
    public bool HasSelection => Selected is not null;
    public bool HasSessions => Sessions.Count > 0;

    public ReportsViewModel(IReportStore store)
    {
        _store = store;
        Reload();
    }

    public void Reload()
    {
        Sessions.Clear();
        foreach (var s in _store.List(string.IsNullOrWhiteSpace(Search) ? null : Search)) Sessions.Add(s);
        OnPropertyChanged(nameof(HasSessions));
    }

    partial void OnSelectedChanged(SessionReport? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        Preview = value is null
            ? "Select a session."
            : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    partial void OnSearchChanged(string value) => Reload();

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        await ExportAsync("json", (report, path) => _store.ExportJsonAsync(report, path));
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        await ExportAsync("csv", (report, path) => _store.ExportCsvAsync(report, path));
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        await ExportAsync("pdf", (report, path) => _store.ExportPdfAsync(report, path));
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        await ExportAsync("zip", (report, path) => _store.ExportBundleAsync(report, path));
    }

    [RelayCommand]
    private async Task AddAttachmentAsync(string sourcePath)
    {
        if (Selected is null || !File.Exists(sourcePath)) return;
        await _store.AddAttachmentAsync(Selected.Id, sourcePath);
        Selected = _store.Get(Selected.Id);
        Reload();
        ExportStatus = $"Attached {Path.GetFileName(sourcePath)}";
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (Selected is null) return;
        var copy = _store.Duplicate(Selected.Id);
        Reload();
        Selected = copy;
        ExportStatus = "Session duplicated.";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is null) return;
        _store.Delete(Selected.Id);
        Selected = null;
        Reload();
    }

    private async Task ExportAsync(string extension, Func<SessionReport, string, Task> exporter)
    {
        if (Selected is null) return;
        var path = ExportPath(extension);
        try
        {
            await exporter(Selected, path);
            ExportStatus = path;
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
    }

    private static string ExportPath(string extension)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortStrider");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
    }
}
