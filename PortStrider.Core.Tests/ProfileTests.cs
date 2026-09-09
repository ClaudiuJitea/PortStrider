using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using Xunit;

namespace PortStrider.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void DefaultProfile_HasTargets()
    {
        var profile = TestProfile.Default();
        Assert.Equal("Default", profile.Name);
        Assert.NotEmpty(profile.Targets);
    }
}

public class CapabilityEnumTests
{
    [Fact]
    public void CapabilityLevels_AreDistinct()
    {
        Assert.Equal(3, Enum.GetValues<CapabilityLevel>().Length);
    }
}
