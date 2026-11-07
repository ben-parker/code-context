using System.Text.Json;
using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers.Projects;

/// <summary>
/// A repository's own files, each naming a share through one of the routes that once skipped
/// the guard. The share is this machine's <c>\\localhost\c$</c> and holds the real file, so
/// reaching it shows up in the result, rather than as a string nobody opened.
/// </summary>
public sealed class RepositoryPathGuardTests : IDisposable
{
    private const string OnLocalShare = @"\\localhost\c$\Windows\notepad.exe";

    /// <summary>These tests read their own temp files back through the share of drive C.</summary>
    private static bool ServesLocalShare
        => File.Exists(OnLocalShare) && Path.GetTempPath().StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase);

    private readonly string _root = Directory.CreateTempSubdirectory("cc-path-guard-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PackageFolder => Path.Combine(_root, "_packages");
    private string DotnetRoot => Path.Combine(_root, "_dotnet");

    private ToolchainLocations Toolchain => new([DotnetRoot], [PackageFolder], [], []);

    private ProjectModel Load(string root, ToolchainLocations toolchain, params string[] files)
        => ProjectModel.Load(new Disk(root, toolchain), root, files, toolchain);

    private ProjectInfo LoadApp() => Assert.Single(Load(_root, Toolchain, Source()).Projects);

    private static string Share(string path) => $@"\\localhost\c$\{path[3..]}";

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Source() => Write("App/A.cs", "class C { }");

    private string App(string body, string attributes = """Sdk="Microsoft.NET.Sdk" """)
    {
        Write("_dotnet/packs/Microsoft.NETCore.App.Ref/8.0.1/ref/net8.0/System.Runtime.dll");
        Directory.CreateDirectory(PackageFolder);
        return Write("App/App.csproj", $"""
            <Project {attributes}>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              {body}
            </Project>
            """);
    }

    /// <summary>A file on the share, whose contents would define REACHED if they were read.</summary>
    private string SdkOnShare(string relative) => Write(relative, """
        <Project>
          <PropertyGroup><DefineConstants>$(DefineConstants);REACHED</DefineConstants></PropertyGroup>
          <ItemGroup><Compile Include="*.cs" /></ItemGroup>
        </Project>
        """);

    private static void AssertNothingReached(ProjectInfo info)
    {
        Assert.DoesNotContain("REACHED", info.PreprocessorSymbols);
        Assert.DoesNotContain(info.MetadataReferences, reference => reference.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private void Assets(string project, Dictionary<string, object> libraries, Dictionary<string, object> target, object[]? downloads = null)
        => Write("App/obj/project.assets.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["targets"] = new Dictionary<string, object> { ["net8.0"] = target },
            ["libraries"] = libraries,
            ["packageFolders"] = new Dictionary<string, object> { [PackageFolder + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath = project },
                ["frameworks"] = new Dictionary<string, object>
                {
                    ["net8.0"] = new { downloadDependencies = downloads ?? [] },
                },
            },
        }));

    [LocalShareFact]
    public void ALibraryPathInRestoreDataIsNotFollowed()
    {
        var project = App(string.Empty);
        var held = Write("_elsewhere/evil/1.0.0/lib/net8.0/Evil.dll");
        var package = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(held)))!;
        Assets(
            project,
            new() { ["Evil/1.0.0"] = new { type = "package", path = Share(package) } },
            new() { ["Evil/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net8.0/Evil.dll"] = new { } } } });

        var info = LoadApp();

        AssertNothingReached(info);
        Assert.Contains("Evil/1.0.0", info.MissingReferences);
    }

    [LocalShareFact]
    public void ACompileKeyInRestoreDataIsNotFollowed()
    {
        var project = App(string.Empty);
        var held = Write("_elsewhere/Evil.dll");
        Assets(
            project,
            new() { ["Evil/1.0.0"] = new { type = "package", path = "evil/1.0.0" } },
            new() { ["Evil/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { [Share(held)] = new { } } } });

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void ADownloadedPackVersionIsNotFollowed()
    {
        var project = App(string.Empty);
        var held = Write("_elsewhere/pack/ref/net8.0/Evil.dll");
        var pack = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(held)))!;
        Assets(project, [], [], [new { name = "Microsoft.NETCore.App.Ref", version = $"[{Share(pack)}]" }]);

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void AnSdkNameIsNotFollowed()
    {
        SdkOnShare("_elsewhere/thesdk/1.0.0/Sdk/Sdk.props");
        App(string.Empty, $"""Sdk="{Share(Path.Combine(_root, "_elsewhere", "thesdk"))}" """);

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void AnSdkVersionIsNotFollowed()
    {
        Directory.CreateDirectory(Path.Combine(PackageFolder, "my.sdk"));
        SdkOnShare("_elsewhere/version/Sdk/Sdk.props");
        App($"""<Sdk Name="My.Sdk" Version="{Share(Path.Combine(_root, "_elsewhere", "version"))}" />""", string.Empty);

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void AnSdkVersionFromGlobalJsonIsNotFollowed()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(PackageFolder, "my.sdk"));
        SdkOnShare("_elsewhere/version/Sdk/Sdk.props");
        Write("global.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["msbuild-sdks"] = new Dictionary<string, string> { ["My.Sdk"] = Share(Path.Combine(_root, "_elsewhere", "version")) },
        }));
        App(string.Empty, """Sdk="My.Sdk" """);

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void APackageNameIsNotFollowed()
    {
        Write("_elsewhere/evil/1.0.0/lib/net8.0/Evil.dll");
        App($"""<ItemGroup><PackageReference Include="{Share(Path.Combine(_root, "_elsewhere", "evil"))}" Version="1.0.0" /></ItemGroup>""");

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void AFrameworkReferenceNameIsNotFollowed()
    {
        Write("_elsewhere/fw.Ref/1.0.0/ref/net8.0/Evil.dll");
        App($"""<ItemGroup><FrameworkReference Include="{Share(Path.Combine(_root, "_elsewhere", "fw"))}" /></ItemGroup>""");

        AssertNothingReached(LoadApp());
    }

    [LocalShareTheory]
    [InlineData(@"\??\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\?\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\.\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"/\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\/localhost\c$\Windows\notepad.exe")]
    public void NoSpellingOfAShareExistsToACondition(string path)
    {
        App($"""<PropertyGroup Condition="Exists('{path}')"><DefineConstants>REACHED</DefineConstants></PropertyGroup>""");

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void AFileAboveCannotBeNamedOnAShare()
    {
        var held = SdkOnShare("_elsewhere/held.props");
        App($"""
            <PropertyGroup><Found>$([MSBuild]::GetPathOfFileAbove('{Share(held)}'))</Found></PropertyGroup>
            <PropertyGroup Condition="'$(Found)' != ''"><DefineConstants>REACHED</DefineConstants></PropertyGroup>
            """);

        AssertNothingReached(LoadApp());
    }

    [LocalShareFact]
    public void NormalizingAProjectPathDoesNotReadTheShare()
    {
        // The runtime resolves a short name against the file system, whichever host it is on.
        App("""
            <PropertyGroup><Full>$([System.IO.Path]::GetFullPath('\\localhost\c$\PROGRA~1'))</Full></PropertyGroup>
            <PropertyGroup Condition="'$(Full)' == '\\localhost\c$\Program Files'"><DefineConstants>REACHED</DefineConstants></PropertyGroup>
            """);

        AssertNothingReached(LoadApp());
    }

    [WindowsFact]
    public void AnUnreachableHostIsNeverWaitedFor()
    {
        // A fresh address each run: Windows remembers a failed host for a while. The tilde is
        // what used to make normalizing the path contact it, before anything tried to open it.
        var host = $"192.0.2.{Random.Shared.Next(2, 250)}";
        App($"""
            <Import Project="\\{host}\share\a~1\x.props" />
            <ItemGroup><Reference Include="X"><HintPath>\\{host}\share\b~1\X.dll</HintPath></Reference></ItemGroup>
            <ItemGroup><ProjectReference Include="\\{host}\share\c~1\Y.csproj" /></ItemGroup>
            """);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var info = LoadApp();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Contains("X", info.MissingReferences);
    }

    [LocalShareFact]
    public void ARepositoryOnAShareKeepsWorking()
    {
        var project = App("""
            <Import Project="..\shared.props" />
            <ItemGroup><Reference Include="Local"><HintPath>..\lib\Local.dll</HintPath></Reference></ItemGroup>
            """);
        SdkOnShare("shared.props");
        Write("lib/Local.dll");
        Write("_packages/held/1.0.0/lib/net8.0/Held.dll");
        Assets(
            Share(project),
            new() { ["Held/1.0.0"] = new { type = "package", path = "held/1.0.0" } },
            new() { ["Held/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net8.0/Held.dll"] = new { } } } });
        var assets = Path.Combine(_root, "App", "obj", "project.assets.json");
        File.WriteAllText(assets, File.ReadAllText(assets).Replace(
            JsonEncodedText.Encode(PackageFolder).ToString(),
            JsonEncodedText.Encode(Share(PackageFolder)).ToString(),
            StringComparison.Ordinal));
        var root = Share(_root);
        var toolchain = new ToolchainLocations([Share(DotnetRoot)], [], [], []);
        Source();

        var info = Assert.Single(Load(root, toolchain, Path.Combine(root, "App", "A.cs")).Projects);

        Assert.Contains("REACHED", info.PreprocessorSymbols);
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("Local.dll", StringComparison.Ordinal));
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("Held.dll", StringComparison.Ordinal));
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("System.Runtime.dll", StringComparison.Ordinal));
        Assert.Equal(ProjectCoverageState.Complete, info.State);
    }

    [LocalShareFact]
    public void ToolchainsOnAShareTheOwnerConfiguredKeepWorking()
    {
        App("""<ItemGroup><PackageReference Include="Held" Version="1.0.0" /></ItemGroup>""");
        Write("_packages/held/1.0.0/lib/net8.0/Held.dll");
        var toolchain = new ToolchainLocations([Share(DotnetRoot)], [Share(PackageFolder)], [], []);

        var info = Assert.Single(Load(_root, toolchain, Source()).Projects);

        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("Held.dll", StringComparison.Ordinal));
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("System.Runtime.dll", StringComparison.Ordinal));
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "These spellings mean something only on Windows.";
        }
    }

    private sealed class LocalShareFactAttribute : FactAttribute
    {
        public LocalShareFactAttribute()
        {
            if (!ServesLocalShare) Skip = "This machine does not serve its administrative share of the temp drive.";
        }
    }

    private sealed class LocalShareTheoryAttribute : TheoryAttribute
    {
        public LocalShareTheoryAttribute()
        {
            if (!ServesLocalShare) Skip = "This machine does not serve its administrative share of the temp drive.";
        }
    }
}
