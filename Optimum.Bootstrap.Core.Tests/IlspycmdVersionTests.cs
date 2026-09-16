using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// Ports the ilspycmd version cases from
/// <c>scripts/tests/install-linux-prerequisites.sh</c>. These are the exact
/// accept and reject values that script pins.
/// </summary>
public class IlspycmdVersionTests
{
    private static readonly IlspycmdCompatibility Range = IlspycmdCompatibility.Fallback;

    [Theory]
    [InlineData("11.0.0.9375")]
    public void AcceptsVersionsInsideTheRange(string version)
    {
        Assert.True(Range.Supports(version));
    }

    [Theory]
    [InlineData("11.0.0.9374")]
    [InlineData("11.0.0.9376")]
    [InlineData("10.1.1.8388")]
    [InlineData("10.1.0.8386")]
    [InlineData("11.0.1.0")]
    [InlineData("11.1.0.0")]
    [InlineData("12.0.0.0")]
    [InlineData("10.2.0.1")]
    [InlineData("11.0.0.9375-rc1")]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("11.0.0")]
    public void RejectsEverythingElse(string version)
    {
        Assert.False(Range.Supports(version));
    }

    [Fact]
    public void ReadsTheRangeAndPinFromConfigFiles()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/.config/ilspycmd-compat.json",
            """{ "minimumVersion": "11.0.0.9375", "maximumVersion": "11.0.0.9375" }""");
        probe.AddFile("/repo/.config/dotnet-tools.json",
            """{ "version": 1, "tools": { "ilspycmd": { "version": "11.0.0.9375" } } }""");

        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(probe, "/repo");

        Assert.Equal("11.0.0.9375", compat.Pin);
        Assert.Equal(new IlspycmdVersion(11, 0, 0, 9375), compat.Minimum);
        Assert.Equal(new IlspycmdVersion(11, 0, 0, 9375), compat.Maximum);
    }

    [Fact]
    public void FallsBackWhenConfigFilesAreAbsent()
    {
        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(new FakeSystemProbe(), "/repo");
        Assert.Equal(IlspycmdCompatibility.Fallback, compat);
    }
}
