using System.Text;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The gate on analyzing only what an edit can affect: however a workspace got into a state,
/// its graph must equal what a fresh scan of that state produces. Each seed walks a workspace
/// of several projects through body-only and top-level edits, in single files and batches,
/// and compares the graph through the real worker, delta applier and store after every step.
/// A failure names the seed, the step and the edits leading to it, and replays exactly.
/// </summary>
public sealed class CSharpWorkerEditWalkConvergenceTests : IAsyncLifetime
{
    private const int Steps = 120;

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-walk-").FullName;
    private CSharpWorkerPipeline _incremental = null!;
    private CSharpWorkerPipeline _fresh = null!;

    public Task InitializeAsync()
    {
        _incremental = new CSharpWorkerPipeline(_root);
        _fresh = new CSharpWorkerPipeline(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _incremental.DisposeAsync();
        await _fresh.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20261104)]
    public async Task EveryStepOfARandomWalkMatchesAFreshScan(int seed)
    {
        var walk = new Walk(_root, seed);
        walk.WriteAll();
        await _incremental.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        await AssertConverged(walk, seed, "initial scan");

        for (var step = 1; step <= Steps; step++)
        {
            var changes = walk.TakeStep();
            await _incremental.GraphUpdateService.ProcessFileChangesAsync(changes, CancellationToken.None);
            await AssertConverged(walk, seed, $"step {step}");
        }

        // A walk that never exercised what it is for proves nothing.
        Assert.True(walk.BodyEdits >= Steps / 4, $"Only {walk.BodyEdits} body edits.");
        Assert.True(walk.TopLevelEdits >= Steps / 4, $"Only {walk.TopLevelEdits} top-level edits.");
    }

    private async Task AssertConverged(Walk walk, int seed, string step)
    {
        await _fresh.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

        var expected = await Facts(_fresh);
        var actual = await Facts(_incremental);
        if (expected.SequenceEqual(actual)) return;

        var message = new StringBuilder($"Seed {seed} diverged at {step}.\n");
        message.AppendLine("Recent edits:").AppendJoin('\n', walk.History.TakeLast(6)).AppendLine();
        message.AppendLine("Missing:").AppendJoin('\n', expected.Except(actual).Take(10)).AppendLine();
        message.AppendLine("Stale:").AppendJoin('\n', actual.Except(expected).Take(10));
        Assert.Fail(message.ToString());
    }

    private static async Task<List<string>> Facts(CSharpWorkerPipeline pipeline)
    {
        var nodes = await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        return nodes.Select(node =>
                $"N {node.Id}|{node.Identifier}|{node.FilePath}|{node.StartLine}:{node.StartCol}-{node.EndLine}:{node.EndCol}|{node.Signature}|{node.Name}|{node.Type}")
            .Concat(edges.Select(edge => $"E {edge.Id}|{edge.SourceId}|{edge.TargetId}|{edge.Type}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A workspace held as a model, so each edit is known to be of the kind it claims.</summary>
    private sealed class Walk(string root, int seed)
    {
        private static readonly string[] LibraryCalls =
        [
            "System.Math.Abs(1);",
            "new System.Random().Next();",
            "new System.Text.StringBuilder().Append(\"x\");",
            "string.Join(\",\", new string[0]);",
            "System.Console.WriteLine();",
            "new System.Collections.Generic.List<int>().Add(1);",
        ];

        private readonly Random _random = new(seed);
        private readonly List<ProjectModel> _projects =
        [
            new("Lib", []),
            new("Mid", ["Lib"]),
            new("App", ["Mid"]),
        ];
        private readonly List<SourceFile> _files = [];
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
        private int _next;

        public int BodyEdits { get; private set; }
        public int TopLevelEdits { get; private set; }
        public List<string> History { get; } = [];

        public void WriteAll()
        {
            foreach (var project in _projects)
            {
                for (var index = 0; index < 3; index++) _files.Add(NewFile(project.Name, "Type" + _next++));
            }

            // A part of a partial type, a file two projects compile, a file in no project,
            // and a same-named type in two projects, all from the start.
            var first = _files[0];
            first.IsPartial = true;
            first.HookBody = Body();
            _files.Add(new SourceFile("Lib", "Part" + _next++, first.TypeName) { IsPartial = true, DeclaresHook = true, Methods = { Method() } });
            _files.Add(new SourceFile("Lib", "Shared", "SharedTool") { Methods = { Method() } });
            _files.Add(new SourceFile("scripts", "Loose", "LooseTool") { Methods = { Method() } });
            _files.Add(new SourceFile("App", "Collides", _files[1].TypeName) { Methods = { Method() } });
            _files.Add(new SourceFile("App", "Program", "AppEntry") { Statements = "Walk.AppEntry.Start();", Methods = { new MethodModel("Start", "") } });

            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>
                """);
            foreach (var project in _projects) WriteProject(project);
            foreach (var file in _files) Write(file);
        }

        private void WriteProject(ProjectModel project)
        {
            var directory = Path.Combine(root, project.Name);
            Directory.CreateDirectory(directory);
            var references = string.Concat(project.References.Select(reference =>
                $"<ProjectReference Include=\"..\\{reference}\\{reference}.csproj\" />"));
            var linked = project.Name == "App" ? "<Compile Include=\"..\\Lib\\Shared.cs\" />" : string.Empty;
            var constants = project.Feature ? "<DefineConstants>$(DefineConstants);FEATURE</DefineConstants>" : string.Empty;
            File.WriteAllText(Path.Combine(directory, project.Name + ".csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework>{constants}</PropertyGroup>
                  <ItemGroup>{references}{linked}</ItemGroup>
                </Project>
                """);
        }

        private string PathOf(SourceFile file) => Path.Combine(root, file.Directory, file.FileName + ".cs");

        private void Write(SourceFile file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(file))!);
            File.WriteAllText(PathOf(file), file.Render());
            _touched.Add(PathOf(file));
        }

        private SourceFile NewFile(string project, string typeName)
            => new(project, typeName, typeName) { Methods = { Method(), Method() } };

        private MethodModel Method() => new("M" + _next++, Body());

        private string Body()
        {
            var statements = new StringBuilder();
            for (var count = _random.Next(0, 3); count > 0; count--)
            {
                statements.Append(Call()).Append(' ');
            }
            return statements.ToString();
        }

        /// <summary>A call into the workspace or a library. A target that goes away just stops binding.</summary>
        private string Call()
        {
            if (_random.Next(3) == 0) return LibraryCalls[_random.Next(LibraryCalls.Length)];
            var candidates = _files.Where(file => file.Methods.Count > 0).ToList();
            if (candidates.Count == 0) return "System.Math.Abs(2);";
            var file = candidates[_random.Next(candidates.Count)];
            var method = file.Methods[_random.Next(file.Methods.Count)];
            return $"new Walk.{file.TypeName}().{method.Name}(Walk.Constants.Value);";
        }

        private SourceFile AnyFile() => _files[_random.Next(_files.Count)];

        private SourceFile FileWithMethods()
            => _files.Where(file => file.Methods.Count > 0).OrderBy(_ => _random.Next()).First();

        public IReadOnlyList<FileChange> TakeStep()
        {
            _touched.Clear();
            var deleted = new List<string>();

            // Mostly single edits, sometimes a batch, as a watcher coalesces them.
            for (var edits = _random.Next(5) == 0 ? 2 : 1; edits > 0; edits--)
            {
                Edit(deleted);
            }

            return _touched.Select(path => new FileChange(path, FileChangeType.Changed))
                .Concat(deleted.Select(path => new FileChange(path, FileChangeType.Deleted)))
                .ToList();
        }

        private void Note(string edit, bool isBody)
        {
            if (isBody) BodyEdits++;
            else TopLevelEdits++;
            History.Add(edit);
        }

        private void Edit(List<string> deleted)
        {
            var roll = _random.Next(100);
            if (roll < 30) EditBody();
            else if (roll < 40) EditHook();
            else if (roll < 46) EditStatements();
            else if (roll < 50) EditComment();
            else if (roll < 58) AddMethod();
            else if (roll < 64) RemoveMethod();
            else if (roll < 70) RenameMethod();
            else if (roll < 74) ChangeParameter();
            else if (roll < 80) ChangeConstant();
            else if (roll < 84) ToggleBase();
            else if (roll < 88) AddFile();
            else if (roll < 92) DeleteFile(deleted);
            else if (roll < 96) CollideNames();
            else ToggleFeature();
        }

        /// <summary>
        /// The body sits in one file while its node belongs to the file declaring it, so a
        /// body-only edit changes an edge the other file holds.
        /// </summary>
        private void EditHook()
        {
            var file = _files.Single(candidate => candidate.HookBody is not null);
            file.HookBody = Body();
            Write(file);
            Note($"partial method body: {file.HookBody}", isBody: true);
        }

        private void EditBody()
        {
            var file = FileWithMethods();
            var method = file.Methods[_random.Next(file.Methods.Count)];
            method.Body = Body();
            Write(file);
            Note($"body of {file.FileName}.{method.Name}: {method.Body}", isBody: true);
        }

        private void EditStatements()
        {
            var file = _files.Single(candidate => candidate.Statements is not null);
            file.Statements = $"System.Math.Abs({_next++}); {Call()}";
            Write(file);
            Note($"statements: {file.Statements}", isBody: true);
        }

        private void EditComment()
        {
            var file = AnyFile();
            file.Comment = "// " + _next++;
            Write(file);
            Note($"comment in {file.FileName}", isBody: true);
        }

        private void AddMethod()
        {
            var file = AnyFile();
            file.Methods.Add(Method());
            Write(file);
            Note($"added {file.FileName}.{file.Methods[^1].Name}", isBody: false);
        }

        private void RemoveMethod()
        {
            var file = FileWithMethods();
            var method = file.Methods[_random.Next(file.Methods.Count)];
            file.Methods.Remove(method);
            Write(file);
            Note($"removed {file.FileName}.{method.Name}", isBody: false);
        }

        private void RenameMethod()
        {
            var file = FileWithMethods();
            var method = file.Methods[_random.Next(file.Methods.Count)];
            var old = method.Name;
            method.Name = "M" + _next++;
            Write(file);
            Note($"renamed {file.FileName}.{old} to {method.Name}", isBody: false);
        }

        private void ChangeParameter()
        {
            var file = FileWithMethods();
            var method = file.Methods[_random.Next(file.Methods.Count)];
            method.Parameter = method.Parameter == "int" ? "long" : "int";
            Write(file);
            Note($"parameter of {file.FileName}.{method.Name} is now {method.Parameter}", isBody: false);
        }

        /// <summary>The value decides whether calls bind to the byte or the long overload.</summary>
        private void ChangeConstant()
        {
            var file = _files[0];
            file.ConstantValue = file.ConstantValue == 1 ? 1000 : 1;
            Write(file);
            Note($"constant is now {file.ConstantValue}", isBody: false);
        }

        private void ToggleBase()
        {
            var file = AnyFile();
            file.Base = file.Base is null ? "System.Exception" : null;
            Write(file);
            Note($"{file.FileName} base is now {file.Base ?? "none"}", isBody: false);
        }

        private void AddFile()
        {
            var project = _projects[_random.Next(_projects.Count)].Name;
            var file = NewFile(project, "Type" + _next++);
            _files.Add(file);
            Write(file);
            Note($"added file {file.FileName} in {project}", isBody: false);
        }

        private void DeleteFile(List<string> deleted)
        {
            var candidates = _files.Where(file => file.CanDelete && file != _files[0]).ToList();
            if (candidates.Count <= 2)
            {
                AddFile();
                return;
            }
            var file = candidates[_random.Next(candidates.Count)];
            _files.Remove(file);
            File.Delete(PathOf(file));
            _touched.Remove(PathOf(file));
            deleted.Add(PathOf(file));
            Note($"deleted {file.FileName}", isBody: false);
        }

        /// <summary>Gives a type the name of one elsewhere, or a fresh one, so identifiers qualify and clear.</summary>
        private void CollideNames()
        {
            var file = _files.Where(candidate => candidate.CanRename).OrderBy(_ => _random.Next()).First();
            var other = AnyFile();
            file.TypeName = _random.Next(2) == 0 && other.Directory != file.Directory
                ? other.TypeName
                : "Type" + _next++;
            Write(file);
            Note($"type in {file.FileName} is now {file.TypeName}", isBody: false);
        }

        /// <summary>A project file changes with no source file changing beside it.</summary>
        private void ToggleFeature()
        {
            var project = _projects[_random.Next(_projects.Count)];
            project.Feature = !project.Feature;
            WriteProject(project);
            _touched.Add(Path.Combine(root, project.Name, project.Name + ".csproj"));
            Note($"{project.Name} FEATURE is now {project.Feature}", isBody: false);
        }
    }

    private sealed record ProjectModel(string Name, string[] References)
    {
        public bool Feature { get; set; }
    }

    private sealed class MethodModel(string name, string body)
    {
        public string Name { get; set; } = name;
        public string Body { get; set; } = body;
        public string Parameter { get; set; } = "int";
    }

    private sealed class SourceFile(string directory, string fileName, string typeName)
    {
        public string Directory { get; } = directory;
        public string FileName { get; } = fileName;
        public string TypeName { get; set; } = typeName;
        public List<MethodModel> Methods { get; } = [];
        public bool IsPartial { get; set; }
        public string? Base { get; set; }
        public string? Statements { get; set; }
        public string Comment { get; set; } = string.Empty;
        public int ConstantValue { get; set; } = 1;
        public bool DeclaresHook { get; set; }
        public string? HookBody { get; set; }

        public bool CanDelete => Statements is null && FileName != "Shared";
        public bool CanRename => Statements is null && !IsPartial;

        public string Render()
        {
            var text = new StringBuilder();
            text.AppendLine(Comment);
            if (Statements is not null) text.AppendLine(Statements);
            text.AppendLine("namespace Walk");
            text.AppendLine("{");
            if (ConstantValue != 1 || FileName.StartsWith("Type0", StringComparison.Ordinal))
            {
                text.AppendLine($"    public static class Constants {{ public const int Value = {ConstantValue}; }}");
            }
            text.Append("    public ").Append(IsPartial ? "partial " : string.Empty).Append("class ").Append(TypeName);
            if (Base is not null) text.Append(" : ").Append(Base);
            text.AppendLine();
            text.AppendLine("    {");
            foreach (var method in Methods)
            {
                text.AppendLine($"        public void {method.Name}({method.Parameter} value) {{ {method.Body}}}");
                text.AppendLine($"        public void {method.Name}(byte value) {{ }}");
            }
            if (DeclaresHook) text.AppendLine("        partial void Hook();");
            if (HookBody is not null) text.AppendLine($"        partial void Hook() {{ {HookBody}}}");
            text.AppendLine("#if FEATURE");
            text.AppendLine($"        public void Feature{FileName}() {{ System.Math.Abs(3); }}");
            text.AppendLine("#else");
            text.AppendLine($"        public void Plain{FileName}() {{ }}");
            text.AppendLine("#endif");
            if (Statements is not null) text.AppendLine("        public static void Start() { }");
            text.AppendLine("    }");
            text.AppendLine("}");
            return text.ToString();
        }
    }
}
