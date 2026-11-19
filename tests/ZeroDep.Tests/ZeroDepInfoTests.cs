using System.Reflection;
using Xunit;

namespace ZeroDep.Tests;

public sealed class ZeroDepInfoTests
{
    [Fact]
    public void VersionMatchesTheAssemblyInformationalVersion()
    {
        string? informational = typeof(ZeroDepInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(informational);

        int plus = informational!.IndexOf('+');
        string expected = plus >= 0 ? informational.Substring(0, plus) : informational;

        Assert.Equal(expected, ZeroDepInfo.Version);
    }

    [Fact]
    public void VersionCarriesNoBuildMetadata()
    {
        Assert.DoesNotContain("+", ZeroDepInfo.Version);
        Assert.StartsWith(typeof(ZeroDepInfo).Assembly.GetName().Version!.ToString(3), ZeroDepInfo.Version);
    }
}
