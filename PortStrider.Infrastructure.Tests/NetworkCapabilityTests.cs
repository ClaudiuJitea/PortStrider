using PortStrider.Infrastructure.Platform;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class NetworkCapabilityTests
{
    [Fact]
    public void FileGrantedNetworkCapabilitiesBecomeInheritableWithoutChangingOtherSets()
    {
        var granted = new PrivilegedProcess.CapabilityData
        {
            Effective = 0x3400, Permitted = 0x3400, Inheritable = 0x80
        };
        var result = PrivilegedProcess.AddNetworkInheritance(granted);
        Assert.Equal(0x3080u, result.Inheritable);
        Assert.Equal(granted.Permitted, result.Permitted);
        Assert.Equal(granted.Effective, result.Effective);
        // CAP_NET_BIND_SERVICE and all other granted capabilities are not propagated.
        Assert.Equal(0u, result.Inheritable & 0x400u);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0x1000u, 0x1000u)]
    [InlineData(0x2000u, 0x2000u)]
    [InlineData(0xffffffffu, 0x3000u)]
    public void OnlyAlreadyPermittedNetworkCapabilitiesAreAdded(uint permitted, uint expected)
    {
        var result = PrivilegedProcess.AddNetworkInheritance(new() { Permitted = permitted });
        Assert.Equal(expected, result.Inheritable);
    }
}
