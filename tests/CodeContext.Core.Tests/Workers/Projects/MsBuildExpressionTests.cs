using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers.Projects;

/// <summary>Conditions and expressions as project files really write them.</summary>
public class MsBuildExpressionTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Configuration"] = "Debug",
        ["Platform"] = "AnyCPU",
        ["TargetFramework"] = "net8.0",
        ["Name"] = "Game.Tools",
        ["Empty"] = "",
    };

    private static MsBuildExpander Expander() => new(name => Values.GetValueOrDefault(name), Path.GetTempPath());

    [Theory]
    [InlineData("'$(Configuration)' == 'Debug'", true)]
    [InlineData("'$(Configuration)' == 'debug'", true)]
    [InlineData(" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ", true)]
    [InlineData("'$(Configuration)|$(Platform)' == 'Release|AnyCPU'", false)]
    [InlineData("'$(Missing)' == ''", true)]
    [InlineData("'$(Configuration)' != 'Debug' Or '$(Platform)' == 'AnyCPU'", true)]
    [InlineData("'$(Configuration)' == 'Debug' And '$(Platform)' == 'x64'", false)]
    [InlineData("!('$(Configuration)' == 'Debug')", false)]
    [InlineData("$(Name.StartsWith('Game'))", true)]
    [InlineData("$(Name.Contains('Nope'))", false)]
    [InlineData("!$(Name.EndsWith('Tools'))", false)]
    [InlineData("'$(Name.ToUpper())' == 'GAME.TOOLS'", true)]
    [InlineData("$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net6.0'))", true)]
    [InlineData("$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))", false)]
    [InlineData("$([MSBuild]::VersionGreaterThanOrEquals('17.8', '17.3'))", true)]
    [InlineData("$([MSBuild]::GetTargetFrameworkIdentifier('$(TargetFramework)')) == '.NETCoreApp'", true)]
    [InlineData("HasTrailingSlash('a/')", true)]
    [InlineData("Exists('no-such-file-here')", false)]
    [InlineData("1 < 2", true)]
    [InlineData("'1.10' > '1.9'", true)]
    public void Evaluates(string condition, bool expected)
        => Assert.Equal(expected, MsBuildCondition.Evaluate(condition, Expander()));

    [Theory]
    [InlineData("$([System.Diagnostics.Process]::Start('calc'))")]
    [InlineData("'$([System.IO.File]::WriteAllText('x', 'y'))' == ''")]
    [InlineData("'$(Name.Frobnicate())' == ''")]
    [InlineData("'a' ==")]
    [InlineData("SomeFunction('a')")]
    public void DoesNotGuessWhatItCannotRead(string condition)
        => Assert.Null(MsBuildCondition.Evaluate(condition, Expander()));

    [Fact]
    public void FalseDecidesAndDespiteDoubt()
        => Assert.False(MsBuildCondition.Evaluate("'a' == 'b' And $(Name.Frobnicate())", Expander()));

    [Fact]
    public void TrueDecidesOrDespiteDoubt()
        => Assert.True(MsBuildCondition.Evaluate("$(Name.Frobnicate()) Or 'a' == 'a'", Expander()));

    [Theory]
    [InlineData("$(Name)", "Game.Tools")]
    [InlineData("pre-$(Name)-post", "pre-Game.Tools-post")]
    [InlineData("$(Name.Replace('.', '_'))", "Game_Tools")]
    [InlineData("$(Name.Substring(5))", "Tools")]
    [InlineData("$([MSBuild]::ValueOrDefault('$(Empty)', 'fallback'))", "fallback")]
    [InlineData("$([System.String]::Copy('$(Configuration)'))", "Debug")]
    [InlineData("no expression", "no expression")]
    public void Expands(string text, string expected)
    {
        var expander = Expander();
        Assert.Equal(expected, expander.Expand(text));
        Assert.False(expander.Uncertain);
    }

    [Fact]
    public void ACallItDoesNotKnowIsEmptyAndFlagged()
    {
        var expander = Expander();
        Assert.Equal("a", expander.Expand("a$([System.Net.Dns]::GetHostName())"));
        Assert.True(expander.Uncertain);
    }

    [Theory]
    [InlineData("net8.0", ".NETCoreApp", "8.0", null)]
    [InlineData("net10.0-windows10.0.19041", ".NETCoreApp", "10.0", "windows")]
    [InlineData("netcoreapp3.1", ".NETCoreApp", "3.1", null)]
    [InlineData("netstandard2.0", ".NETStandard", "2.0", null)]
    [InlineData("net472", ".NETFramework", "4.7.2", null)]
    [InlineData("net48", ".NETFramework", "4.8.0", null)]
    [InlineData(".NETCoreApp,Version=v8.0", ".NETCoreApp", "8.0", null)]
    public void ParsesTargetFrameworks(string moniker, string identifier, string version, string? platform)
    {
        Assert.True(TargetFrameworkName.TryParse(moniker, out var framework));
        Assert.Equal(identifier, framework.Identifier);
        Assert.Equal(Version.Parse(version), framework.Version);
        Assert.Equal(platform, framework.Platform);
    }

    [Theory]
    [InlineData("net8.0", "NET8_0", "NET8_0_OR_GREATER", "NET5_0_OR_GREATER", "NETCOREAPP", "NETCOREAPP3_1_OR_GREATER")]
    [InlineData("netstandard2.0", "NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_6_OR_GREATER")]
    [InlineData("net48", "NETFRAMEWORK", "NET48", "NET472_OR_GREATER", "NET48_OR_GREATER")]
    [InlineData("netcoreapp3.1", "NETCOREAPP", "NETCOREAPP3_1", "NETCOREAPP2_0_OR_GREATER")]
    [InlineData("net6.0-windows", "NET6_0", "WINDOWS", "WINDOWS7_0", "WINDOWS7_0_OR_GREATER")]
    [InlineData("net8.0-windows10.0.19041.0", "WINDOWS", "WINDOWS10_0_19041_0", "WINDOWS10_0_19041_0_OR_GREATER", "WINDOWS10_0_17763_0_OR_GREATER", "WINDOWS7_0_OR_GREATER")]
    [InlineData("net8.0-windows10.0.19041", "WINDOWS10_0_19041_0")]
    [InlineData("net8.0-android34.0", "ANDROID", "ANDROID34_0", "ANDROID34_0_OR_GREATER")]
    public void DefinesTheSdkFrameworkSymbols(string moniker, params string[] expected)
    {
        Assert.True(TargetFrameworkName.TryParse(moniker, out var framework));
        var symbols = framework.PreprocessorSymbols().ToList();
        foreach (var symbol in expected) Assert.Contains(symbol, symbols);
    }

    [Fact]
    public void AnOlderPlatformVersionDoesNotClaimANewerOne()
    {
        Assert.True(TargetFrameworkName.TryParse("net8.0-windows10.0.17763.0", out var framework));
        Assert.DoesNotContain("WINDOWS10_0_19041_0_OR_GREATER", framework.PreprocessorSymbols());
    }

    [Theory]
    [InlineData("net8.0", "netstandard2.0", true)]
    [InlineData("net8.0", "net9.0", false)]
    [InlineData("net48", "netstandard2.0", true)]
    [InlineData("net48", "netstandard2.1", false)]
    [InlineData("net45", "netstandard2.0", false)]
    [InlineData("net8.0", "net48", false)]
    [InlineData("net8.0", "net6.0-windows", false)]
    public void KnowsWhatAFrameworkCanConsume(string project, string asset, bool expected)
    {
        Assert.True(TargetFrameworkName.TryParse(project, out var target));
        Assert.True(TargetFrameworkName.TryParse(asset, out var candidate));
        Assert.Equal(expected, target.CanConsume(candidate));
    }
}
