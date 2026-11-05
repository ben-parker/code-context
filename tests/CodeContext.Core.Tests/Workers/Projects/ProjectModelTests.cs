using System.Text.Json;
using System.Xml;
using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers.Projects;

/// <summary>
/// Each test lays out a project shape on disk and reads it. No build runs, and every toolchain
/// location is a directory of this fixture, so nothing depends on what this machine has
/// installed: the empty files stand in for assemblies, whose content the model never opens.
/// </summary>
public class ProjectModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-project-model-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string PackageFolder => Path.Combine(_root, "_packages");
    private string DotnetRoot => Path.Combine(_root, "_dotnet");
    private string ReferenceRoot => Path.Combine(_root, "_reference");

    private ToolchainLocations Toolchain => new([DotnetRoot], [PackageFolder], [ReferenceRoot], []);

    private string Write(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Source(string relative) => Write(relative, "class C { }");

    private void InstallRefPack(string pack, string version, string folder, params string[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Write($"_dotnet/packs/{pack}/{version}/ref/{folder}/{assembly}.dll");
        }
    }

    private void InstallCoreFramework(string folder = "net8.0", string version = "8.0.1")
        => InstallRefPack("Microsoft.NETCore.App.Ref", version, folder, "System.Runtime", "System.Linq");

    private string Package(string id, string version, string asset)
        => Write($"_packages/{id.ToLowerInvariant()}/{version}/{asset}");

    /// <summary>A minimal assets file, built as JSON so paths are escaped as NuGet escapes them.</summary>
    private string Assets(string relative, string projectPath, string framework, params (string Id, string Version, string Asset)[] packages)
    {
        var libraries = packages.ToDictionary(
            package => $"{package.Id}/{package.Version}",
            package => (object)new { type = "package", path = $"{package.Id.ToLowerInvariant()}/{package.Version}" });
        var target = packages.ToDictionary(
            package => $"{package.Id}/{package.Version}",
            package => (object)new
            {
                type = "package",
                compile = new Dictionary<string, object> { [package.Asset] = new { } },
            });
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object> { [framework] = target },
            ["libraries"] = libraries,
            ["packageFolders"] = new Dictionary<string, object> { [PackageFolder + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath },
                ["frameworks"] = new Dictionary<string, object>
                {
                    [framework] = new
                    {
                        frameworkReferences = new Dictionary<string, object>
                        {
                            ["Microsoft.NETCore.App"] = new { privateAssets = "all" },
                        },
                    },
                },
            },
        });
        return Write(relative, json);
    }

    private ProjectModel Load(params string[] files) => ProjectModel.Load(_root, files, Toolchain);

    private const string Net8 = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    [Fact]
    public void ARepositoryWithoutProjectsLeavesEveryFileUnassigned()
    {
        var file = Source("src/A.cs");

        var model = Load(file);

        Assert.Empty(model.Projects);
        Assert.Equal([file], model.UnassignedFiles);
        Assert.Null(model.OwnerOf(file));
    }

    [Fact]
    public void ReadsPackageAssetsFromRestoreData()
    {
        InstallCoreFramework();
        var project = Write("App/App.csproj", Net8);
        var assembly = Package("Newtonsoft.Json", "13.0.1", "lib/net6.0/Newtonsoft.Json.dll");
        Assets("App/obj/project.assets.json", project, "net8.0", ("Newtonsoft.Json", "13.0.1", "lib/net6.0/Newtonsoft.Json.dll"));
        var file = Source("App/Program.cs");

        var info = Assert.Single(Load(file).Projects);

        Assert.Equal(ProjectCoverageState.Complete, info.State);
        Assert.True(info.HasRestoreData);
        Assert.Contains(assembly, info.MetadataReferences);
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("System.Runtime.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyMarkerAssetIsNotAReference()
    {
        InstallCoreFramework();
        var project = Write("App/App.csproj", Net8);
        Assets("App/obj/project.assets.json", project, "net8.0", ("Marker", "1.0.0", "lib/net8.0/_._"));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Complete, info.State);
        Assert.DoesNotContain(info.MetadataReferences, reference => reference.Contains("marker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindsRestoreDataByTheProjectItNamesNotByItsLocation()
    {
        InstallCoreFramework();
        var project = Write("game/Game.csproj", Net8);
        var assembly = Package("Engine", "4.0.0", "lib/net8.0/Engine.dll");
        Assets("game/.engine/mono/temp/obj/project.assets.json", project, "net8.0", ("Engine", "4.0.0", "lib/net8.0/Engine.dll"));

        var info = Assert.Single(Load(Source("game/Player.cs")).Projects);

        Assert.True(info.HasRestoreData);
        Assert.Contains(assembly, info.MetadataReferences);
    }

    [Fact]
    public void RestoreDataForAnotherProjectIsNotBorrowed()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);
        Assets("elsewhere/obj/project.assets.json", Path.Combine(_root, "elsewhere", "Other.csproj"), "net8.0");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.False(info.HasRestoreData);
    }

    [Fact]
    public void AMovedRepositoryStillUsesRestoreDataBesideTheProject()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);
        var assembly = Package("Lib", "1.0.0", "lib/net8.0/Lib.dll");
        Assets("App/obj/project.assets.json", "Z:/old/location/App/App.csproj", "net8.0", ("Lib", "1.0.0", "lib/net8.0/Lib.dll"));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(assembly, info.MetadataReferences);
    }

    [Fact]
    public void AnEvaluatedIntermediatePathIsHonored()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <MSBuildProjectExtensionsPath>build/ext/</MSBuildProjectExtensionsPath>
              </PropertyGroup>
            </Project>
            """);
        var assembly = Package("Lib", "1.0.0", "lib/net8.0/Lib.dll");
        Assets("App/build/ext/project.assets.json", "Z:/moved/App.csproj", "net8.0", ("Lib", "1.0.0", "lib/net8.0/Lib.dll"));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(assembly, info.MetadataReferences);
    }

    [Fact]
    public void WithoutRestoreDataDeclaredPackagesAreLookedUpAndTheGapIsReported()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Present" Version="2.0.0" />
                <PackageReference Include="Absent" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var older = Package("Present", "2.0.0", "lib/netstandard2.0/Present.dll");
        var best = Package("Present", "2.0.0", "lib/net6.0/Present.dll");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Partial, info.State);
        Assert.False(info.HasRestoreData);
        Assert.Contains(best, info.MetadataReferences);
        Assert.DoesNotContain(older, info.MetadataReferences);
        Assert.Equal(["Absent"], info.MissingReferences);
        Assert.Contains(info.Notes, note => note.Contains("No restore data", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutRestoreDataWhatAReferenceLacksIsAGapHereToo()
    {
        InstallCoreFramework();
        Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Absent" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);

        var model = Load(Source("Lib/L.cs"), Source("App/A.cs"));

        Assert.Equal(ProjectCoverageState.Partial, model.Projects.Single(project => project.Name == "App").State);
    }

    [Fact]
    public void ABuildOnlyPackageIsFoundEvenThoughItHasNothingToCompileAgainst()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Tool" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        Package("Tool", "1.0.0", "lib/net6.0/_._");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Empty(info.MissingReferences);
    }

    [Fact]
    public void CentralPackageVersionsAreRead()
    {
        InstallCoreFramework();
        Write("Directory.Packages.props", """
            <Project><ItemGroup><PackageVersion Include="Pinned" Version="3.0.0" /></ItemGroup></Project>
            """);
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Pinned" /></ItemGroup>
            </Project>
            """);
        Package("Pinned", "2.0.0", "lib/net6.0/Pinned.dll");
        var pinned = Package("Pinned", "3.0.0", "lib/net6.0/Pinned.dll");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(pinned, info.MetadataReferences);
    }

    [Fact]
    public void AProjectWithNoPackagesIsCompleteWithoutRestoreData()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Complete, info.State);
    }

    [Fact]
    public void NoFrameworkFoundIsAFallbackAndSaysSo()
    {
        Write("App/App.csproj", Net8);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Fallback, info.State);
        Assert.Contains(info.Notes, note => note.Contains("worker's own runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectDeclaringNoFrameworkIsAFallback()
    {
        Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Fallback, info.State);
        Assert.Null(info.TargetFramework);
    }

    [Fact]
    public void AnInstalledRuntimeStandsInForAMissingRefPackAndIsReported()
    {
        Write("App/App.csproj", Net8);
        var fixture = Path.Combine(_root, "_dotnet", "shared", "Microsoft.NETCore.App", "8.0.5");
        Directory.CreateDirectory(fixture);
        File.Copy(typeof(object).Assembly.Location, Path.Combine(fixture, "System.Private.CoreLib.dll"));
        Write("_dotnet/shared/Microsoft.NETCore.App/8.0.5/native.dll", "not an assembly");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Partial, info.State);
        Assert.Single(info.MetadataReferences);
        Assert.Contains(info.Notes, note => note.Contains("installed runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableProjectLeavesItsFilesUnassignedAndIsReported()
    {
        Write("Broken/Broken.csproj", "<Project><not closed");
        var file = Source("Broken/A.cs");

        var model = Load(file);

        Assert.Empty(model.Projects);
        Assert.Equal([file], model.UnassignedFiles);
        var failure = Assert.Single(model.Failures);
        Assert.Equal("Broken/Broken.csproj", failure.RelativePath);
    }

    [Fact]
    public void ABrokenProjectElsewhereIsNotReported()
    {
        InstallCoreFramework();
        Write("Broken/Broken.csproj", "<Project><not closed");
        Write("App/App.csproj", Net8);

        Assert.Empty(Load(Source("App/A.cs")).Failures);
    }

    [Fact]
    public void AnOldStyleProjectTakesItsFilesConstantsAndHintPathsFromTheFile()
    {
        var engine = Write("Engine/Managed/Engine.Core.dll");
        Write("Assembly-CSharp.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
                <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
                <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                <LangVersion>9.0</LangVersion>
                <NoStdLib>true</NoStdLib>
                <AssemblyName>Assembly-CSharp</AssemblyName>
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
                <DefineConstants>ENGINE_2022;DEBUG;TRACE</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
                <DefineConstants>SHIPPING</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Assets\Scripts\Player.cs" />
                <Compile Include="Assets\Scripts\Enemy.cs" />
              </ItemGroup>
              <ItemGroup>
                <Reference Include="Engine.Core"><HintPath>Engine\Managed\Engine.Core.dll</HintPath></Reference>
                <Reference Include="Gone"><HintPath>Engine\Managed\Gone.dll</HintPath></Reference>
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);
        var player = Source("Assets/Scripts/Player.cs");
        var unlisted = Source("Assets/Scripts/Unlisted.cs");

        var model = Load(player, unlisted);

        var info = Assert.Single(model.Projects);
        Assert.Equal(info, model.OwnerOf(player));
        Assert.Equal([unlisted], model.UnassignedFiles);
        Assert.Contains("ENGINE_2022", info.PreprocessorSymbols);
        Assert.DoesNotContain("SHIPPING", info.PreprocessorSymbols);
        Assert.Equal("9.0", info.LanguageVersion);
        Assert.Equal("net471", info.TargetFramework);
        Assert.Equal([engine], info.MetadataReferences);
        Assert.Equal(["Gone"], info.MissingReferences);
        Assert.Equal(ProjectCoverageState.Partial, info.State);
    }

    [Fact]
    public void AHintPathToASiblingAssemblyBecomesAProjectReference()
    {
        InstallCoreFramework();
        Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>My.Lib</AssemblyName></PropertyGroup>
            </Project>
            """);
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Reference Include="My.Lib"><HintPath>..\Lib\bin\Debug\My.Lib.dll</HintPath></Reference></ItemGroup>
            </Project>
            """);

        var model = Load(Source("Lib/L.cs"), Source("App/A.cs"));

        var app = model.Projects.Single(project => project.Name == "App");
        Assert.Equal(["Lib"], app.ProjectReferences.Select(project => project.Name));
        Assert.Empty(app.MissingReferences);
    }

    [Fact]
    public void AFrameworkAssemblyNamedBareIsNotMissing()
    {
        Write("_reference/v4.8/mscorlib.dll");
        Write("_reference/v4.8/System.Xml.dll");
        Write("Old/Old.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup>
                <Compile Include="*.cs" />
                <Reference Include="System.Xml" />
                <Reference Include="Unknown.Thing, Version=1.0.0.0, Culture=neutral" />
              </ItemGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("Old/A.cs")).Projects);

        Assert.Equal(["Unknown.Thing"], info.MissingReferences);
    }

    [Fact]
    public void AVersionDirectoryHoldingOnlyDocumentationIsNotAFramework()
    {
        Write("_reference/v4.6.1/mscorlib.xml");
        Write("_reference/v4.7.2/mscorlib.dll");
        Write("Old/Old.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.6.1</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup><Compile Include="*.cs" /></ItemGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("Old/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Partial, info.State);
        Assert.Contains(info.Notes, note => note.Contains("no reference assemblies", StringComparison.Ordinal));
    }

    private string AssetsJson(string projectPath, string targetKey, string alias, object framework, object? libraries = null)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["targets"] = new Dictionary<string, object> { [targetKey] = libraries ?? new { } },
            ["libraries"] = new { },
            ["packageFolders"] = new Dictionary<string, object> { [PackageFolder + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath },
                ["frameworks"] = new Dictionary<string, object> { [alias] = framework },
            },
        });

    [Fact]
    public void AFrameworkReferenceToAPartOfAPackFindsThePack()
    {
        InstallCoreFramework();
        InstallRefPack("Microsoft.WindowsDesktop.App.Ref", "8.0.1", "net8.0", "PresentationCore");
        var project = Write("App/App.csproj", Net8);
        Write("App/obj/project.assets.json", AssetsJson(project, "net8.0", "net8.0", new
        {
            frameworkReferences = new Dictionary<string, object>
            {
                ["Microsoft.NETCore.App"] = new { },
                ["Microsoft.WindowsDesktop.App.WPF"] = new { },
            },
        }));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Empty(info.MissingReferences);
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("PresentationCore.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void AFrameworkReferenceNamingAPackAndProfileFindsWhatTheRestoreDownloaded()
    {
        InstallCoreFramework();
        var sdk = Package("Microsoft.Windows.SDK.NET.Ref", "10.0.26100.57", "lib/net8.0/Microsoft.Windows.SDK.NET.dll");
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0-windows10.0.26100.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("App/obj/project.assets.json", AssetsJson(project, "net8.0-windows10.0.26100.0", "net8.0-windows10.0.26100.0", new
        {
            frameworkReferences = new Dictionary<string, object>
            {
                ["Microsoft.NETCore.App"] = new { },
                ["Microsoft.Windows.SDK.NET.Ref.Windows"] = new { },
            },
            downloadDependencies = new[] { new { name = "Microsoft.Windows.SDK.NET.Ref", version = "[10.0.26100.57, 10.0.26100.57]" } },
        }));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Empty(info.MissingReferences);
        Assert.Contains(sdk, info.MetadataReferences);
        Assert.Contains("WINDOWS", info.PreprocessorSymbols);
        Assert.Contains("WINDOWS10_0_26100_0", info.PreprocessorSymbols);
        Assert.Contains("WINDOWS10_0_26100_0_OR_GREATER", info.PreprocessorSymbols);
    }

    [Fact]
    public void AFrameworkAPackageBringsReachesTheProject()
    {
        InstallCoreFramework();
        InstallRefPack("Microsoft.AspNetCore.App.Ref", "8.0.1", "net8.0", "Microsoft.AspNetCore");
        var project = Write("App/App.csproj", Net8);
        Write("App/obj/project.assets.json", AssetsJson(project, "net8.0", "net8.0", new { }, new Dictionary<string, object>
        {
            ["Web.Lib/1.0.0"] = new { type = "package", frameworkReferences = new[] { "Microsoft.AspNetCore.App" } },
        }));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("Microsoft.AspNetCore.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetWithAPlatformIsNotConfusedWithTheSameVersionWithout()
    {
        InstallCoreFramework();
        var windowsOnly = Package("Windows.Only", "1.0.0", "lib/net8.0/Windows.Only.dll");
        var plain = Package("Plain", "1.0.0", "lib/net8.0/Plain.dll");
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0-windows;net8.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        Write("App/obj/project.assets.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["targets"] = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Plain/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net8.0/Plain.dll"] = new { } } },
                },
                ["net8.0-windows7.0"] = new Dictionary<string, object>
                {
                    ["Windows.Only/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net8.0/Windows.Only.dll"] = new { } } },
                },
            },
            ["libraries"] = new Dictionary<string, object>
            {
                ["Plain/1.0.0"] = new { type = "package", path = "plain/1.0.0" },
                ["Windows.Only/1.0.0"] = new { type = "package", path = "windows.only/1.0.0" },
            },
            ["packageFolders"] = new Dictionary<string, object> { [PackageFolder + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath = project },
                ["frameworks"] = new Dictionary<string, object> { ["net8.0-windows"] = new { }, ["net8.0"] = new { } },
            },
        }));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(windowsOnly, info.MetadataReferences);
        Assert.DoesNotContain(plain, info.MetadataReferences);
    }

    [Fact]
    public void ALegacyLongFormTargetKeyIsFound()
    {
        Write("_reference/v4.8/mscorlib.dll");
        var assembly = Package("Lib", "1.0.0", "lib/net45/Lib.dll");
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("App/obj/project.assets.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["targets"] = new Dictionary<string, object>
            {
                [".NETFramework,Version=v4.8"] = new Dictionary<string, object>
                {
                    ["Lib/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net45/Lib.dll"] = new { } } },
                },
            },
            ["libraries"] = new Dictionary<string, object> { ["Lib/1.0.0"] = new { type = "package", path = "lib/1.0.0" } },
            ["packageFolders"] = new Dictionary<string, object> { [PackageFolder + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath = project },
                ["frameworks"] = new Dictionary<string, object> { ["net48"] = new { } },
            },
        }));

        Assert.Contains(assembly, Assert.Single(Load(Source("App/A.cs")).Projects).MetadataReferences);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("13.0", "13.0.0")]
    [InlineData("1.2.3.0", "1.2.3")]
    public void ADeclaredVersionFindsTheFolderNuGetNormalizedItTo(string declared, string folder)
    {
        InstallCoreFramework();
        Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Lib" Version="{declared}" /></ItemGroup>
            </Project>
            """);
        var assembly = Package("Lib", folder, "lib/net6.0/Lib.dll");

        Assert.Contains(assembly, Assert.Single(Load(Source("App/A.cs")).Projects).MetadataReferences);
    }

    [Fact]
    public void AnSdkStyleFrameworkProjectSeesItsFullVersion()
    {
        Write("_reference/v4.7.2/mscorlib.dll");
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net472</TargetFramework></PropertyGroup>
              <ItemGroup Condition="'$(TargetFrameworkVersion)' == 'v4.7.2'"><Compile Remove="Skipped.cs" /></ItemGroup>
            </Project>
            """);
        var skipped = Source("App/Skipped.cs");

        Assert.Equal([skipped], Load(skipped).UnassignedFiles);
    }

    [Fact]
    public void AnImportCannotReachAboveTheRepository()
    {
        InstallCoreFramework();
        var outer = Directory.CreateTempSubdirectory("cc-above-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outer, "Directory.Build.props"), """
                <Project><PropertyGroup><DefineConstants>$(DefineConstants);ABOVE</DefineConstants></PropertyGroup></Project>
                """);
            var repository = Path.Combine(outer, "repo");
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var app = Path.Combine(repository, "App");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(repository, "Directory.Build.props"), """
                <Project><Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" /></Project>
                """);
            File.WriteAllText(Path.Combine(app, "App.csproj"), Net8);
            var file = Path.Combine(app, "A.cs");
            File.WriteAllText(file, "class C { }");

            var info = Assert.Single(ProjectModel.Load(repository, [file], Toolchain).Projects);

            Assert.DoesNotContain("ABOVE", info.PreprocessorSymbols);
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    [Fact]
    public void AMultiTargetedProjectUsesItsFirstTargetAndReportsTheOthers()
    {
        InstallCoreFramework();
        Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0;net48</TargetFrameworks>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <DefineConstants>$(DefineConstants);MODERN</DefineConstants>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net48'">
                <DefineConstants>$(DefineConstants);LEGACY</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("Lib/L.cs")).Projects);

        Assert.Equal("net8.0", info.TargetFramework);
        Assert.Equal(["netstandard2.0", "net48"], info.InactiveTargetFrameworks);
        Assert.Contains("MODERN", info.PreprocessorSymbols);
        Assert.DoesNotContain("LEGACY", info.PreprocessorSymbols);
        Assert.Contains("NET8_0", info.PreprocessorSymbols);
    }

    [Fact]
    public void ProjectReferencesAreTransitiveAndDependenciesComeFirst()
    {
        InstallCoreFramework();
        Write("C/C.csproj", Net8);
        Write("B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\C\C.csproj" /></ItemGroup>
            </Project>
            """);
        Write("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\B\B.csproj" /></ItemGroup>
            </Project>
            """);

        var model = Load(Source("A/A.cs"), Source("B/B.cs"), Source("C/C.cs"));

        Assert.Equal(["C", "B", "A"], model.Projects.Select(project => project.Name));
        var a = model.Projects.Single(project => project.Name == "A");
        Assert.Equal(["B", "C"], a.ProjectReferences.Select(project => project.Name));
    }

    [Fact]
    public void RestoredProjectReferencesCountEvenIfTheProjectFileNamesNone()
    {
        InstallCoreFramework();
        Write("Lib/Lib.csproj", Net8);
        var app = Write("App/App.csproj", Net8);
        var assets = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["targets"] = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object> { ["Lib/1.0.0"] = new { type = "project" } },
            },
            ["libraries"] = new Dictionary<string, object>
            {
                ["Lib/1.0.0"] = new { type = "project", path = "../Lib/Lib.csproj", msbuildProject = "../Lib/Lib.csproj" },
            },
            ["packageFolders"] = new Dictionary<string, object>(),
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath = app },
                ["frameworks"] = new Dictionary<string, object> { ["net8.0"] = new { } },
            },
        });
        Write("App/obj/project.assets.json", assets);

        var model = Load(Source("Lib/L.cs"), Source("App/A.cs"));

        Assert.Equal(["Lib"], model.Projects.Single(project => project.Name == "App").ProjectReferences.Select(project => project.Name));
    }

    [Fact]
    public void AReferenceCycleIsBrokenAndReported()
    {
        InstallCoreFramework();
        Write("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\B\B.csproj" /></ItemGroup>
            </Project>
            """);
        Write("B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\A\A.csproj" /></ItemGroup>
            </Project>
            """);

        var model = Load(Source("A/A.cs"), Source("B/B.cs"));

        Assert.Equal(2, model.Projects.Count);
        Assert.Contains(model.Projects, project => project.Notes.Any(note => note.Contains("cycle", StringComparison.Ordinal)));
        foreach (var project in model.Projects)
        {
            var position = model.Projects.ToList().IndexOf(project);
            Assert.All(project.ProjectReferences, reference => Assert.True(model.Projects.ToList().IndexOf(reference) < position));
        }
    }

    [Fact]
    public void AReferencedProjectWithNoIndexedFilesBindsThroughItsBuildOutput()
    {
        InstallCoreFramework();
        Write("Lib/Lib.csproj", Net8);
        var output = Write("Lib/bin/Debug/net8.0/Lib.dll");
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains(output, info.MetadataReferences);
        Assert.Empty(info.MissingReferences);
    }

    [Fact]
    public void AReferencedProjectWithNoFilesOrOutputIsMissing()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(["Lib"], info.MissingReferences);
        Assert.Equal(ProjectCoverageState.Partial, info.State);
    }

    [Fact]
    public void DefaultGlobsSkipBuildOutputAndHiddenDirectories()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);
        var kept = Source("App/Nested/Deep/A.cs");
        var inBin = Source("App/bin/Debug/Generated.cs");
        var inObj = Source("App/obj/Debug/Generated.cs");
        var hidden = Source("App/.cache/Generated.cs");

        var model = Load(kept, inBin, inObj, hidden);

        Assert.NotNull(model.OwnerOf(kept));
        Assert.Equal([inBin, inObj, hidden], model.UnassignedFiles);
    }

    [Fact]
    public void CompileRemoveExcludesAndAnotherProjectClaimsTheFile()
    {
        InstallCoreFramework();
        Write("game/Game.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="rules\Rules\Rules.csproj" />
                <Compile Remove="rules\**" />
              </ItemGroup>
            </Project>
            """);
        Write("game/rules/Rules/Rules.csproj", Net8);
        var rule = Source("game/rules/Rules/Rule.cs");
        var script = Source("game/Script.cs");

        var model = Load(rule, script);

        Assert.Equal("Rules", model.OwnerOf(rule)!.Name);
        Assert.Single(model.ProjectsCompiling(rule));
        Assert.Equal("Game", model.OwnerOf(script)!.Name);
    }

    [Fact]
    public void ANestedProjectOwnsItsFilesEvenThoughTheParentGlobCompilesThem()
    {
        InstallCoreFramework();
        Write("Parent.csproj", Net8);
        Write("Child/Child.csproj", Net8);
        var file = Source("Child/C.cs");

        var model = Load(file);

        Assert.Equal(["Child", "Parent"], model.ProjectsCompiling(file).Select(project => project.Name));
    }

    [Fact]
    public void ALinkedFileIsCompiledByBothProjectsAndOwnedByItsNearestOne()
    {
        InstallCoreFramework();
        Write("Shared/Shared.csproj", Net8);
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="..\Shared\Common.cs" Link="Common.cs" /></ItemGroup>
            </Project>
            """);
        var file = Source("Shared/Common.cs");

        var model = Load(file);

        Assert.Equal("Shared", model.OwnerOf(file)!.Name);
        Assert.Equal(2, model.ProjectsCompiling(file).Count);
    }

    [Fact]
    public void AWildcardIncludeReachesOutsideTheProjectDirectory()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="..\Common\**\*.cs" Exclude="..\Common\Skip\**" /></ItemGroup>
            </Project>
            """);
        var included = Source("Common/Sub/A.cs");
        var excluded = Source("Common/Skip/B.cs");

        var model = Load(included, excluded);

        Assert.Equal("App", model.OwnerOf(included)!.Name);
        Assert.Equal([excluded], model.UnassignedFiles);
    }

    [Fact]
    public void DisablingDefaultItemsLeavesOnlyExplicitOnes()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="Kept.cs" /></ItemGroup>
            </Project>
            """);
        var kept = Source("App/Kept.cs");
        var other = Source("App/Other.cs");

        var model = Load(kept, other);

        Assert.Equal([other], model.UnassignedFiles);
    }

    [Fact]
    public void TwoProjectsOfOneNameGetUniqueNamesAndKeys()
    {
        InstallCoreFramework();
        Write("a/Tests.csproj", Net8);
        Write("b/Tests.csproj", Net8);

        var model = Load(Source("a/A.cs"), Source("b/B.cs"));

        Assert.Equal(["a/Tests", "b/Tests"], model.Projects.Select(project => project.Name).Order());
        Assert.Equal(2, model.Projects.Select(project => project.Key).Distinct().Count());
    }

    [Fact]
    public void AProjectKeyDependsOnlyOnItsPlaceInTheWorkspace()
    {
        InstallCoreFramework();
        Write("a/App.csproj", Net8);

        var before = Assert.Single(Load(Source("a/A.cs")).Projects).Key;
        Write("b/Other.csproj", Net8);
        var after = Load(Source("a/A.cs"), Source("b/B.cs")).Projects.Single(project => project.Name == "App").Key;

        Assert.Equal(before, after);
        Assert.Matches("^[0-9a-f]{8}$", before);
    }

    [Fact]
    public void DirectoryBuildPropsAndTargetsApplyInOrder()
    {
        InstallCoreFramework();
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <DefineConstants>$(DefineConstants);FROM_PROPS</DefineConstants>
                <LangVersion>12.0</LangVersion>
              </PropertyGroup>
            </Project>
            """);
        Write("Directory.Build.targets", """
            <Project>
              <PropertyGroup><DefineConstants>$(DefineConstants);FROM_TARGETS;$(ProjectValue)</DefineConstants></PropertyGroup>
            </Project>
            """);
        Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ProjectValue>FROM_PROJECT</ProjectValue><LangVersion>11.0</LangVersion></PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("src/App/A.cs")).Projects);

        Assert.Equal("net8.0", info.TargetFramework);
        Assert.Equal("11.0", info.LanguageVersion);
        Assert.Contains("FROM_PROPS", info.PreprocessorSymbols);
        Assert.Contains("FROM_TARGETS", info.PreprocessorSymbols);
        Assert.Contains("FROM_PROJECT", info.PreprocessorSymbols);
    }

    [Fact]
    public void AParentDirectoryBuildPropsIsReachedOnlyThroughAnExplicitChain()
    {
        InstallCoreFramework();
        Write("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);OUTER</DefineConstants></PropertyGroup></Project>
            """);
        Write("src/Directory.Build.props", """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><DefineConstants>$(DefineConstants);INNER</DefineConstants></PropertyGroup>
            </Project>
            """);
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var info = Assert.Single(Load(Source("src/App/A.cs")).Projects);

        Assert.Contains("INNER", info.PreprocessorSymbols);
        Assert.DoesNotContain("OUTER", info.PreprocessorSymbols);

        File.WriteAllText(Path.Combine(_root, "src", "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><DefineConstants>$(DefineConstants);INNER</DefineConstants></PropertyGroup>
            </Project>
            """);

        info = Assert.Single(Load(Source("src/App/A.cs")).Projects);

        Assert.Contains("OUTER", info.PreprocessorSymbols);
    }

    [Fact]
    public void SettingsAboveARepositoryDoNotApply()
    {
        InstallCoreFramework();
        var outer = Directory.CreateTempSubdirectory("cc-outer-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outer, "Directory.Build.props"), """
                <Project><PropertyGroup><DefineConstants>$(DefineConstants);ABOVE</DefineConstants></PropertyGroup></Project>
                """);
            var repository = Path.Combine(outer, "repo");
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var app = Path.Combine(repository, "src", "App");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "App.csproj"), Net8);
            var file = Path.Combine(app, "A.cs");
            File.WriteAllText(file, "class C { }");

            var info = Assert.Single(ProjectModel.Load(app, [file], Toolchain).Projects);

            Assert.DoesNotContain("ABOVE", info.PreprocessorSymbols);
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    [Fact]
    public void AWorkspaceOnASubdirectoryStillSeesItsRepositorySettings()
    {
        InstallCoreFramework();
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write("Directory.Build.props", """
            <Project><PropertyGroup><DefineConstants>$(DefineConstants);REPOSITORY</DefineConstants></PropertyGroup></Project>
            """);
        Write("src/App/App.csproj", Net8);
        var file = Source("src/App/A.cs");

        var info = Assert.Single(ProjectModel.Load(Path.Combine(_root, "src"), [file], Toolchain).Projects);

        Assert.Contains("REPOSITORY", info.PreprocessorSymbols);
    }

    [Fact]
    public void AThirdPartySdkIsReadAsDataFromItsPackage()
    {
        InstallCoreFramework();
        Write("_packages/engine.net.sdk/4.2.0/Sdk/Sdk.props", """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)\Versions.props" />
              <PropertyGroup>
                <Configurations>Debug;ExportDebug;ExportRelease</Configurations>
                <BaseIntermediateOutputPath>$(MSBuildProjectDirectory)\.engine\obj\</BaseIntermediateOutputPath>
                <EngineImportsNetSdk Condition=" '$(UsingMicrosoftNETSdk)' != 'true' ">true</EngineImportsNetSdk>
                <EnginePlatform Condition=" '$([MSBuild]::IsOsPlatform(Windows))' ">win</EnginePlatform>
                <EnginePlatform Condition=" '$(EnginePlatform)' == '' ">other</EnginePlatform>
              </PropertyGroup>
              <Import Sdk="Microsoft.NET.Sdk" Project="Sdk.props" Condition=" '$(EngineImportsNetSdk)' == 'true' " />
            </Project>
            """);
        Write("_packages/engine.net.sdk/4.2.0/Sdk/Versions.props", """
            <Project><PropertyGroup><EngineVersion>ENGINE4_2</EngineVersion></PropertyGroup></Project>
            """);
        Write("_packages/engine.net.sdk/4.2.0/Sdk/Sdk.targets", """
            <Project>
              <Import Sdk="Microsoft.NET.Sdk" Project="Sdk.targets" Condition=" '$(EngineImportsNetSdk)' == 'true' " />
              <PropertyGroup>
                <DefineConstants Condition=" '$(Configuration)' == 'Debug' ">$(DefineConstants);EDITOR</DefineConstants>
                <DefineConstants>ENGINE;$(EngineVersion);$(DefineConstants)</DefineConstants>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="EngineSharp" Version="4.2.0" /></ItemGroup>
            </Project>
            """);
        Write("game/Game.csproj", """
            <Project Sdk="Engine.NET.Sdk/4.2.0">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var engine = Package("EngineSharp", "4.2.0", "lib/net8.0/EngineSharp.dll");
        var script = Source("game/Script.cs");
        var generated = Source("game/.engine/obj/Generated.cs");

        var model = Load(script, generated);

        var info = Assert.Single(model.Projects);
        Assert.Contains("EDITOR", info.PreprocessorSymbols);
        Assert.Contains("ENGINE", info.PreprocessorSymbols);
        Assert.Contains("ENGINE4_2", info.PreprocessorSymbols);
        Assert.Contains("DEBUG", info.PreprocessorSymbols);
        Assert.Contains(engine, info.MetadataReferences);
        Assert.Equal([generated], model.UnassignedFiles);
    }

    [Fact]
    public void AnSdkVersionCanComeFromGlobalJson()
    {
        InstallCoreFramework();
        Write("global.json", """{ "msbuild-sdks": { "Pinned.Sdk": "2.0.0" } }""");
        Write("_packages/pinned.sdk/1.0.0/Sdk/Sdk.props", """
            <Project><PropertyGroup><DefineConstants>OLD</DefineConstants></PropertyGroup></Project>
            """);
        Write("_packages/pinned.sdk/2.0.0/Sdk/Sdk.props", """
            <Project><PropertyGroup><DefineConstants>NEW</DefineConstants><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
            """);
        Write("_packages/pinned.sdk/2.0.0/Sdk/Sdk.targets", "<Project></Project>");
        Write("App/App.csproj", """
            <Project Sdk="Pinned.Sdk"><ItemGroup><Compile Include="*.cs" /></ItemGroup></Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains("NEW", info.PreprocessorSymbols);
        Assert.DoesNotContain("OLD", info.PreprocessorSymbols);
    }

    [Fact]
    public void AnUnknownSdkFallsBackToCommonConventionsAndIsReported()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Nowhere.Sdk/1.0.0">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var file = Source("App/A.cs");

        var model = Load(file);

        var info = Assert.Single(model.Projects);
        Assert.Equal(info, model.OwnerOf(file));
        Assert.Equal(ProjectCoverageState.Partial, info.State);
        Assert.Contains(info.Notes, note => note.Contains("Nowhere.Sdk", StringComparison.Ordinal));
    }

    [Fact]
    public void ImplicitUsingsComeFromTheProjectAndItsUsingItems()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <Using Remove="System.Net.Http" />
                <Using Include="My.Namespace" />
                <Using Include="My.Static" Static="true" />
                <Using Include="My.Type" Alias="Short" />
              </ItemGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains("global using global::System.Linq;", info.GlobalUsings);
        Assert.Contains("global using global::My.Namespace;", info.GlobalUsings);
        Assert.Contains("global using static global::My.Static;", info.GlobalUsings);
        Assert.Contains("global using Short = global::My.Type;", info.GlobalUsings);
        Assert.DoesNotContain("System.Net.Http", info.GlobalUsings);
    }

    [Fact]
    public void AProjectWithoutImplicitUsingsHasNone()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);

        Assert.Empty(Assert.Single(Load(Source("App/A.cs")).Projects).GlobalUsings);
    }

    [Fact]
    public void AWebProjectGetsItsSdkUsingsAndFramework()
    {
        InstallCoreFramework();
        InstallRefPack("Microsoft.AspNetCore.App.Ref", "8.0.1", "net8.0", "Microsoft.AspNetCore");
        Write("Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("Web/A.cs")).Projects);

        Assert.Contains("global using global::Microsoft.AspNetCore.Builder;", info.GlobalUsings);
        Assert.Contains(info.MetadataReferences, reference => reference.EndsWith("Microsoft.AspNetCore.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBuildsGeneratedUsingsWinWhenTheyAreCurrent()
    {
        InstallCoreFramework();
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddHours(-1));
        Write("App/obj/Debug/net8.0/App.GlobalUsings.g.cs", "global using global::From.The.Build;\n");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains("From.The.Build", info.GlobalUsings);
    }

    [Fact]
    public void StaleGeneratedUsingsAreIgnored()
    {
        InstallCoreFramework();
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        var generated = Write("App/obj/Debug/net8.0/App.GlobalUsings.g.cs", "global using global::Stale;\n");
        File.SetLastWriteTimeUtc(generated, File.GetLastWriteTimeUtc(project).AddHours(-1));

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.DoesNotContain("Stale", info.GlobalUsings);
        Assert.Contains("System.Linq", info.GlobalUsings);
    }

    [Fact]
    public void AnUnreadableConditionIsRecordedAndCountsAsTrue()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="$([System.Net.Dns]::GetHostName()) == 'x'">
                <DefineConstants>$(DefineConstants);GUESSED</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Contains("GUESSED", info.PreprocessorSymbols);
        Assert.Contains(info.Notes, note => note.Contains("could not be read", StringComparison.Ordinal));
        Assert.Equal(ProjectCoverageState.Complete, info.State);
    }

    [Fact]
    public void AConfigurationOtherThanDebugIsUsedWhenAProjectHasNoDebug()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Configurations>Staging;Live</Configurations>
              </PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal("Staging", info.Configuration);
        Assert.Contains("STAGING", info.PreprocessorSymbols);
        Assert.DoesNotContain("DEBUG", info.PreprocessorSymbols);
    }

    [Fact]
    public void ChooseTakesTheFirstMatchingBranch()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <PropertyGroup><DefineConstants>$(DefineConstants);FIRST</DefineConstants></PropertyGroup>
                </When>
                <When Condition="'$(Configuration)' == 'Debug'">
                  <PropertyGroup><DefineConstants>$(DefineConstants);SECOND</DefineConstants></PropertyGroup>
                </When>
                <Otherwise>
                  <PropertyGroup><DefineConstants>$(DefineConstants);THIRD</DefineConstants></PropertyGroup>
                </Otherwise>
              </Choose>
            </Project>
            """);

        var symbols = Assert.Single(Load(Source("App/A.cs")).Projects).PreprocessorSymbols;

        Assert.Contains("SECOND", symbols);
        Assert.DoesNotContain("FIRST", symbols);
        Assert.DoesNotContain("THIRD", symbols);
    }

    [Fact]
    public void ItemsSeePropertiesSetAfterThemInTheFile()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Remove="$(Skipped)\**" /></ItemGroup>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><Skipped>legacy</Skipped></PropertyGroup>
            </Project>
            """);
        var skipped = Source("App/legacy/Old.cs");

        Assert.Equal([skipped], Load(skipped).UnassignedFiles);
    }

    [Fact]
    public void AnImportCycleEnds()
    {
        InstallCoreFramework();
        Write("a.props", "<Project><Import Project=\"b.props\" /></Project>");
        Write("b.props", "<Project><Import Project=\"a.props\" /></Project>");
        Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="a.props" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Single(Load(Source("A.cs")).Projects);
    }

    [Fact]
    public void AMissingImportMakesAProjectPartial()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\build\shared.props" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.Equal(ProjectCoverageState.Partial, info.State);
    }

    [Fact]
    public void AToolsetImportIsNotAGap()
    {
        InstallCoreFramework();
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(MSBuildExtensionsPath)\Custom\Thing.targets" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Equal(ProjectCoverageState.Complete, Assert.Single(Load(Source("App/A.cs")).Projects).State);
    }

    [Fact]
    public void NoticesAChangedInputAndARestoreThatArrived()
    {
        InstallCoreFramework();
        var project = Write("App/App.csproj", Net8);
        var model = Load(Source("App/A.cs"));
        Assert.False(model.InputsChanged());

        File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddMinutes(5));
        Assert.True(model.InputsChanged());

        model = Load(Source("App/A.cs"));
        Assert.False(model.InputsChanged());

        Assets("App/obj/project.assets.json", project, "net8.0");
        Assert.True(model.InputsChanged());
    }

    [Fact]
    public void MalformedRestoreDataIsTreatedAsAbsent()
    {
        InstallCoreFramework();
        Write("App/App.csproj", Net8);
        Write("App/obj/project.assets.json", "{ not json");

        var info = Assert.Single(Load(Source("App/A.cs")).Projects);

        Assert.False(info.HasRestoreData);
        Assert.Equal(ProjectCoverageState.Complete, info.State);
    }

    [Fact]
    public void ADoctypeIsRefused()
    {
        var path = Write("App/App.csproj", "<!DOCTYPE p [<!ENTITY x \"y\">]><Project>&x;</Project>");

        Assert.ThrowsAny<XmlException>(() => ProjectEvaluator.Evaluate(
            path, _root, [], new Dictionary<string, string>()));
    }
}
