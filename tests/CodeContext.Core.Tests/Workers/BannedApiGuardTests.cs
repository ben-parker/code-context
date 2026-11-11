using System.Reflection;
using CodeContext.CSharp.Worker.Projects;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The compiler keeps the worker off the raw file system, but only while the ban is in force.
/// These fail if someone turns it down: an entry dropped from the list, the warning no longer
/// an error, or the suppression spreading past the one class allowed to hold it.
/// </summary>
public sealed class BannedApiGuardTests
{
    private const string Rule = "RS0030";

    /// <summary>Every entry of the list. An entry is added or dropped here as well, or a test fails.</summary>
    private static readonly string[] Required =
    [
        "T:System.IO.File;",
        "T:System.IO.Directory;",
        "T:System.IO.FileInfo;",
        "T:System.IO.DirectoryInfo;",
        "T:System.IO.FileSystemInfo;",
        "T:System.IO.FileStream;",
        "T:System.IO.FileSystemWatcher;",
        "T:System.IO.DriveInfo;",
        "T:System.IO.MemoryMappedFiles.MemoryMappedFile;",
        "T:System.IO.Enumeration.FileSystemEnumerable`1;",
        "T:System.IO.Enumeration.FileSystemEnumerator`1;",
        "T:System.Diagnostics.FileVersionInfo;",
        "M:System.IO.Path.GetFullPath(System.String);",
        "M:System.IO.Path.GetFullPath(System.String,System.String);",
        "M:System.IO.Path.GetRelativePath(System.String,System.String);",
        "M:System.IO.Path.Exists(System.String);",
        "M:System.IO.Path.GetTempFileName;",
        "M:System.IO.StreamReader.#ctor(System.String);",
        "M:System.IO.StreamReader.#ctor(System.String,System.Boolean);",
        "M:System.IO.StreamReader.#ctor(System.String,System.IO.FileStreamOptions);",
        "M:System.IO.StreamReader.#ctor(System.String,System.Text.Encoding);",
        "M:System.IO.StreamReader.#ctor(System.String,System.Text.Encoding,System.Boolean);",
        "M:System.IO.StreamReader.#ctor(System.String,System.Text.Encoding,System.Boolean,System.Int32);",
        "M:System.IO.StreamReader.#ctor(System.String,System.Text.Encoding,System.Boolean,System.IO.FileStreamOptions);",
        "M:System.IO.StreamWriter.#ctor(System.String);",
        "M:System.IO.StreamWriter.#ctor(System.String,System.Boolean);",
        "M:System.IO.StreamWriter.#ctor(System.String,System.IO.FileStreamOptions);",
        "M:System.IO.StreamWriter.#ctor(System.String,System.Boolean,System.Text.Encoding);",
        "M:System.IO.StreamWriter.#ctor(System.String,System.Boolean,System.Text.Encoding,System.Int32);",
        "M:System.IO.StreamWriter.#ctor(System.String,System.Text.Encoding,System.IO.FileStreamOptions);",
        "M:System.Xml.XmlReader.Create(System.String);",
        "M:System.Xml.XmlReader.Create(System.String,System.Xml.XmlReaderSettings);",
        "M:System.Xml.XmlReader.Create(System.String,System.Xml.XmlReaderSettings,System.Xml.XmlParserContext);",
        "T:System.Xml.XmlTextReader;",
        "M:System.Xml.XmlDocument.Load(System.String);",
        "M:System.Xml.Linq.XDocument.Load(System.String);",
        "M:System.Xml.Linq.XDocument.Load(System.String,System.Xml.Linq.LoadOptions);",
        "M:System.Xml.Linq.XElement.Load(System.String);",
        "M:System.Xml.Linq.XElement.Load(System.String,System.Xml.Linq.LoadOptions);",
        "M:System.Xml.XPath.XPathDocument.#ctor(System.String);",
        "M:System.Xml.XPath.XPathDocument.#ctor(System.String,System.Xml.XmlSpace);",
        "T:System.IO.RandomAccess;",
        "T:System.IO.IsolatedStorage.IsolatedStorageFile;",
        "T:System.IO.Compression.ZipFile;",
        "T:System.IO.Compression.ZipFileExtensions;",
        "T:System.Xml.XmlTextWriter;",
        "M:System.Xml.XmlWriter.Create(System.String);",
        "M:System.Xml.XmlWriter.Create(System.String,System.Xml.XmlWriterSettings);",
        "M:System.Xml.XmlDocument.Save(System.String);",
        "M:System.Xml.Linq.XDocument.Save(System.String);",
        "M:System.Xml.Linq.XDocument.Save(System.String,System.Xml.Linq.SaveOptions);",
        "M:System.Xml.Linq.XElement.Save(System.String);",
        "M:System.Xml.Linq.XElement.Save(System.String,System.Xml.Linq.SaveOptions);",
        "T:Microsoft.CodeAnalysis.FileSystemExtensions;",
        "M:Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(System.String,Microsoft.CodeAnalysis.MetadataReferenceProperties,Microsoft.CodeAnalysis.DocumentationProvider);",
        "M:Microsoft.CodeAnalysis.AssemblyMetadata.CreateFromFile(System.String);",
        "M:Microsoft.CodeAnalysis.ModuleMetadata.CreateFromFile(System.String);",
        "T:Microsoft.CodeAnalysis.XmlFileResolver;",
        "T:Microsoft.CodeAnalysis.SourceFileResolver;",
        "T:Microsoft.CodeAnalysis.MetadataReferenceResolver;",
        "M:Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions.WithXmlReferenceResolver(Microsoft.CodeAnalysis.XmlReferenceResolver);",
        "M:Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions.WithSourceReferenceResolver(Microsoft.CodeAnalysis.SourceReferenceResolver);",
        "M:Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions.WithMetadataReferenceResolver(Microsoft.CodeAnalysis.MetadataReferenceResolver);",
        "M:System.Reflection.Assembly.Load(System.String);",
        "M:System.Reflection.Assembly.Load(System.Reflection.AssemblyName);",
        "M:System.Reflection.Assembly.Load(System.Byte[]);",
        "M:System.Reflection.Assembly.Load(System.Byte[],System.Byte[]);",
        "M:System.Reflection.Assembly.LoadFile(System.String);",
        "M:System.Reflection.Assembly.LoadFrom(System.String);",
        "M:System.Reflection.Assembly.LoadFrom(System.String,System.Byte[],System.Configuration.Assemblies.AssemblyHashAlgorithm);",
        "M:System.Reflection.Assembly.UnsafeLoadFrom(System.String);",
        "M:System.Reflection.AssemblyName.GetAssemblyName(System.String);",
        "M:System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(System.String);",
        "M:System.Runtime.Loader.AssemblyLoadContext.LoadFromNativeImagePath(System.String,System.String);",
        "T:System.Runtime.InteropServices.NativeLibrary;",
        "T:Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference;",
        "T:System.Diagnostics.Process;",
        "T:System.Net.Http.HttpClient;",
        "T:System.Net.Http.HttpMessageInvoker;",
        "T:System.Net.WebClient;",
        "T:System.Net.WebRequest;",
        "T:System.Net.Dns;",
        "T:System.Net.Sockets.Socket;",
        "T:System.Net.Sockets.TcpClient;",
        "T:System.Net.Sockets.UdpClient;",
        "T:System.Net.NetworkInformation.Ping;",
    ];

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeContext.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("The repository root was not found above the test output.");
    }

    private static string Worker => Path.Combine(RepositoryRoot(), "src", "CodeContext.CSharp.Worker");

    /// <summary>Everything that could speak for the worker's build, skipping build output.</summary>
    private static IEnumerable<string> WorkerBuildInputs()
    {
        var root = RepositoryRoot();
        var above = new[] { "Directory.Build.props", "Directory.Build.targets", ".editorconfig", ".globalconfig" }
            .Select(name => Path.Combine(root, name))
            .Concat(new[] { "Directory.Build.props", "Directory.Build.targets", ".editorconfig", ".globalconfig" }
                .Select(name => Path.Combine(root, "src", name)))
            .Where(File.Exists);
        var inside = Directory.EnumerateFiles(Worker, "*", SearchOption.AllDirectories).Where(path =>
        {
            var relative = Path.GetRelativePath(Worker, path).Replace('\\', '/');
            return !relative.StartsWith("bin/", StringComparison.Ordinal) && !relative.StartsWith("obj/", StringComparison.Ordinal);
        });
        return above.Concat(inside);
    }

    [Fact]
    public void TheListIsExactlyTheBansPinnedHere()
    {
        var entries = File.ReadAllLines(Path.Combine(Worker, "BannedSymbols.txt"));

        var banned = entries
            .Where(entry => entry.Length > 0 && !entry.StartsWith('#'))
            .Select(entry => entry[..(entry.IndexOf(';') + 1)]);

        Assert.Equal(Required.Order(StringComparer.Ordinal), banned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBanIsAnErrorInTheWorker()
    {
        var project = File.ReadAllText(Path.Combine(Worker, "CodeContext.CSharp.Worker.csproj"));

        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", project);
        Assert.Contains("""<AdditionalFiles Include="BannedSymbols.txt" />""", project);
        Assert.Contains($"<WarningsAsErrors>$(WarningsAsErrors);{Rule}", project);
    }

    [Fact]
    public void OnlyDiskSuppressesTheBan()
    {
        var mentions = WorkerBuildInputs()
            .Where(path => !path.EndsWith("BannedSymbols.txt", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(Rule, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal);

        Assert.Equal(["CodeContext.CSharp.Worker.csproj", "Disk.cs"], mentions);
    }

    [Fact]
    public void TheProjectFileOnlyRaisesTheBan()
    {
        var lines = File.ReadAllLines(Path.Combine(Worker, "CodeContext.CSharp.Worker.csproj"))
            .Where(line => line.Contains(Rule, StringComparison.OrdinalIgnoreCase));

        Assert.All(lines, line => Assert.Contains("<WarningsAsErrors>", line));
    }

    [Fact]
    public void NoFileEscapesTheAnalyzerAsGeneratedCode()
    {
        // The analyzer skips generated code, so this would be a quiet way around it.
        var escaping = WorkerBuildInputs().Where(path => path.EndsWith(".cs", StringComparison.Ordinal)).Where(path =>
        {
            var name = Path.GetFileName(path);
            return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
                || File.ReadAllText(path).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
                || File.ReadAllText(path).Contains("GeneratedCode", StringComparison.Ordinal);
        });

        Assert.Empty(escaping);
    }

    [Fact]
    public void RoslynIsGivenNothingThatOpensFiles()
    {
        // Each of these would open what source text names, such as a doc comment's include.
        var options = (CSharpCompilationOptions)typeof(Disk).Assembly
            .GetType("CodeContext.CSharp.Worker.Projects.ProjectCompilation")!
            .GetField("Options", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        Assert.Null(options.XmlReferenceResolver);
        Assert.Null(options.SourceReferenceResolver);
        Assert.Null(options.MetadataReferenceResolver);
    }
}
