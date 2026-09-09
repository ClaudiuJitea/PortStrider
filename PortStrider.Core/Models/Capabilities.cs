using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class FeatureCapability
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CapabilityLevel Level { get; init; }
    public required string Summary { get; init; }
    public string? Prerequisite { get; init; }
}

public sealed class AdapterCapabilities
{
    public required string AdapterId { get; init; }
    public required string AdapterName { get; init; }
    public IReadOnlyList<FeatureCapability> Features { get; init; } = [];
}
