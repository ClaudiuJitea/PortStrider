using PortStrider.Infrastructure.Platform;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class ProcessUtilTests
{
    [Fact]
    public void FindCommand_UsesStandardLinuxPaths()
    {
        if (!OperatingSystem.IsLinux()) return;

        if (File.Exists("/usr/bin/apt-get"))
            Assert.Equal("/usr/bin/apt-get", ProcessUtil.FindCommand("apt-get"));
        if (File.Exists("/usr/bin/pkexec"))
            Assert.Equal("/usr/bin/pkexec", ProcessUtil.FindCommand("pkexec"));
    }
}
