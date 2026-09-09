using System.Text.Json;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.Infrastructure.Profiles;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly List<TestProfile> _profiles = [];
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public JsonProfileStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortStrider", "profiles");
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        Load();
        if (_profiles.Count == 0)
            _profiles.Add(TestProfile.Default());
    }

    public IReadOnlyList<TestProfile> List() => _profiles.OrderBy(p => p.Name).ToArray();

    public TestProfile? Get(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);

    public TestProfile? GetDefault() => _profiles.FirstOrDefault(p => p.Id == TestProfile.Default().Id) ?? _profiles.FirstOrDefault();

    public void Save(TestProfile profile)
    {
        var idx = _profiles.FindIndex(p => p.Id == profile.Id);
        var updated = profile with { UpdatedAt = DateTimeOffset.Now };
        if (idx >= 0) _profiles[idx] = updated;
        else _profiles.Add(updated);
        Persist();
    }

    public void Delete(Guid id)
    {
        if (id == TestProfile.Default().Id) return;
        _profiles.RemoveAll(p => p.Id == id);
        Persist();
    }

    public TestProfile Duplicate(Guid id, string newName)
    {
        var source = Get(id) ?? throw new InvalidOperationException("Profile not found.");
        var copy = source with { Id = Guid.NewGuid(), Name = newName, UpdatedAt = DateTimeOffset.Now };
        _profiles.Add(copy);
        Persist();
        return copy;
    }

    public Task ExportAsync(TestProfile profile, string path, CancellationToken cancellationToken = default)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(profile, _json));
        return Task.CompletedTask;
    }

    public Task<TestProfile> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var profile = JsonSerializer.Deserialize<TestProfile>(File.ReadAllText(path), _json)
            ?? throw new InvalidOperationException("Invalid profile file.");
        var imported = profile with { Id = Guid.NewGuid(), Name = $"{profile.Name} (import)", UpdatedAt = DateTimeOffset.Now };
        Save(imported);
        return Task.FromResult(imported);
    }

    private void Load()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<TestProfile>>(File.ReadAllText(_indexPath), _json);
            if (loaded is not null) _profiles.AddRange(loaded);
        }
        catch
        {
            // corrupt index
        }
    }

    private void Persist() => File.WriteAllText(_indexPath, JsonSerializer.Serialize(_profiles, _json));
}
