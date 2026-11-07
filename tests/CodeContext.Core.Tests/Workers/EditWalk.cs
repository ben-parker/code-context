using System.Text;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A workspace of several projects held as a model, so each edit is known to be of the kind it claims,
/// and a seeded walk through edits of it. The convergence gates walk it and compare the graph with a
/// fresh scan after every step; a seed replays exactly.
///
/// The extended walk adds what a restart has to notice: files added, removed and renamed, a constant
/// toggled in a project file, a project reference added and removed, an edit of
/// <c>Directory.Build.props</c>, a touch that changes nothing, and down steps, where several edits reach
/// the disk and no event reaches anyone (the caller ignores the changes a down step returns).
/// </summary>
internal sealed class EditWalk(string root, int seed, bool extended = false)
{
    /// <summary>Kinds of edit the extended walk must each have made, counted in <see cref="Kinds"/>.</summary>
    public static readonly string[] ExtendedKinds =
    [
        "body", "top-level", "add file", "delete file", "rename file", "project constant",
        "project reference", "Directory.Build.props", "touch", "down step",
    ];

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
    private bool _propsFeature;

    public int BodyEdits { get; private set; }
    public int TopLevelEdits { get; private set; }
    public List<string> History { get; } = [];

    /// <summary>How many edits of each kind were made (extended kinds; body and top-level always).</summary>
    public Dictionary<string, int> Kinds { get; } = new(StringComparer.Ordinal);

    private string PropsPath => Path.Combine(root, "Directory.Build.props");

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
        _files.Add(new SourceFile(Path.Combine("Lib", "Sub"), "Nested", "NestedTool") { Methods = { Method() } });
        _files.Add(new SourceFile("App", "Program", "AppEntry") { Statements = "Walk.AppEntry.Start();", Methods = { new MethodModel("Start", "") } });

        WriteProps();
        foreach (var project in _projects) WriteProject(project);
        foreach (var file in _files) Write(file);
    }

    private void WriteProps()
    {
        var feature = _propsFeature ? "<DefineConstants>$(DefineConstants);FEATURE</DefineConstants>" : string.Empty;
        File.WriteAllText(PropsPath, $"""
            <Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings>{feature}</PropertyGroup></Project>
            """);
    }

    private void WriteProject(ProjectModel project)
    {
        var directory = Path.Combine(root, project.Name);
        Directory.CreateDirectory(directory);
        var references = string.Concat(project.References.Select(reference =>
            $"<ProjectReference Include=\"..\\{reference}\\{reference}.csproj\" />"));
        var linked = project.Name == "App" ? "<Compile Include=\"..\\Lib\\Shared.cs\" />" : string.Empty;
        if (project.LinkedFile is { } link) linked += $"<Compile Include=\"{link}\" />";
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

    /// <summary>One step as a watcher would deliver it: mostly a single edit, sometimes a batch of two.</summary>
    public IReadOnlyList<FileChange> TakeStep()
    {
        _touched.Clear();
        var deleted = new List<string>();

        for (var edits = _random.Next(5) == 0 ? 2 : 1; edits > 0; edits--)
        {
            Edit(deleted);
        }

        return Changes(deleted);
    }

    /// <summary>
    /// Several edits that reach the disk while nothing is running: no event is delivered for them, so
    /// only a restart's verification can find them. Returns nothing to deliver.
    /// </summary>
    public void TakeDownStep()
    {
        _touched.Clear();
        var deleted = new List<string>();
        for (var edits = _random.Next(2, 5); edits > 0; edits--)
        {
            Edit(deleted);
        }
        Count("down step");
        History.Add("(down)");
    }

    private List<FileChange> Changes(List<string> deleted)
        => _touched.Select(path => new FileChange(path, FileChangeType.Changed))
            .Concat(deleted.Select(path => new FileChange(path, FileChangeType.Deleted)))
            .ToList();

    private void Count(string kind) => Kinds[kind] = Kinds.GetValueOrDefault(kind) + 1;

    private void Note(string edit, bool isBody, string? kind = null)
    {
        if (isBody) BodyEdits++;
        else TopLevelEdits++;
        Count(isBody ? "body" : "top-level");
        if (kind is not null) Count(kind);
        History.Add(edit);
    }

    private void Edit(List<string> deleted)
    {
        var roll = _random.Next(100);
        if (extended)
        {
            ExtendedEdit(roll, deleted);
            return;
        }

        // The original distribution, kept so the walks of the gates that predate the extension replay.
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
        else if (roll < 91) CollideNames();
        else if (roll < 93) ToggleFeature();
        else if (roll < 97) ToggleLink();
        else ToggleNestedProject(deleted);
    }

    /// <summary>
    /// The extended walk's first edits are one of each kind a restart must notice, in a seeded order, so
    /// even a short walk makes every kind; then edits follow the distribution.
    /// </summary>
    private Queue<int>? _firstKinds;

    private void ExtendedEdit(int roll, List<string> deleted)
    {
        _firstKinds ??= new Queue<int>(new[] { 61, 65, 69, 76, 80, 83, 87 }.OrderBy(_ => _random.Next()));
        if (_firstKinds.TryDequeue(out var first)) roll = first;

        if (roll < 22) EditBody();
        else if (roll < 28) EditHook();
        else if (roll < 32) EditStatements();
        else if (roll < 35) EditComment();
        else if (roll < 41) AddMethod();
        else if (roll < 45) RemoveMethod();
        else if (roll < 49) RenameMethod();
        else if (roll < 52) ChangeParameter();
        else if (roll < 56) ChangeConstant();
        else if (roll < 59) ToggleBase();
        else if (roll < 63) AddFile();
        else if (roll < 67) DeleteFile(deleted);
        else if (roll < 71) RenameFile(deleted);
        else if (roll < 74) CollideNames();
        else if (roll < 78) ToggleFeature();
        else if (roll < 82) ToggleReference();
        else if (roll < 85) ToggleProps();
        else if (roll < 90) Touch();
        else if (roll < 95) ToggleLink();
        else ToggleNestedProject(deleted);
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
        Note($"added file {file.FileName} in {project}", isBody: false, "add file");
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
        Note($"deleted {file.FileName}", isBody: false, "delete file");
    }

    /// <summary>The same code under a new name, possibly in another project's directory.</summary>
    private void RenameFile(List<string> deleted)
    {
        var candidates = _files.Where(file => file.CanDelete && file != _files[0]).ToList();
        if (candidates.Count == 0)
        {
            AddFile();
            return;
        }
        var file = candidates[_random.Next(candidates.Count)];
        var renamed = file.MovedTo(_projects[_random.Next(_projects.Count)].Name, "Renamed" + _next++);
        _files[_files.IndexOf(file)] = renamed;
        File.Delete(PathOf(file));
        _touched.Remove(PathOf(file));
        deleted.Add(PathOf(file));
        Write(renamed);
        Note($"renamed file {file.FileName} to {renamed.Directory}/{renamed.FileName}", isBody: false, "rename file");
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

    /// <summary>A project borrows or returns another's file, which can change who owns it.</summary>
    private void ToggleLink()
    {
        var project = _projects[_random.Next(_projects.Count)];
        if (project.LinkedFile is null)
        {
            var file = _files.Where(candidate => candidate.Directory != project.Name && candidate.Directory != "scripts")
                .OrderBy(_ => _random.Next())
                .First();
            project.LinkedFile = $"..\\{file.Directory}\\{file.FileName}.cs";
        }
        else
        {
            project.LinkedFile = null;
        }
        WriteProject(project);
        _touched.Add(Path.Combine(root, project.Name, project.Name + ".csproj"));
        Note($"{project.Name} now links {project.LinkedFile ?? "nothing"}", isBody: false);
    }

    /// <summary>A project appears over part of another's tree and takes the files under it.</summary>
    private void ToggleNestedProject(List<string> deleted)
    {
        var path = Path.Combine(root, "Lib", "Sub", "Sub.csproj");
        if (File.Exists(path))
        {
            File.Delete(path);
            deleted.Add(path);
            Note("nested project removed", isBody: false);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _touched.Add(path);
        Note("nested project added", isBody: false);
    }

    /// <summary>A project file changes with no source file changing beside it.</summary>
    private void ToggleFeature()
    {
        var project = _projects[_random.Next(_projects.Count)];
        project.Feature = !project.Feature;
        WriteProject(project);
        _touched.Add(Path.Combine(root, project.Name, project.Name + ".csproj"));
        Note($"{project.Name} FEATURE is now {project.Feature}", isBody: false, "project constant");
    }

    /// <summary>Mid stops or starts referencing Lib, so its calls into Lib, and App's through it, bind or fail.</summary>
    private void ToggleReference()
    {
        var mid = _projects.Single(project => project.Name == "Mid");
        mid.References = mid.References.Length == 0 ? ["Lib"] : [];
        WriteProject(mid);
        _touched.Add(Path.Combine(root, mid.Name, mid.Name + ".csproj"));
        Note($"Mid now references {(mid.References.Length == 0 ? "nothing" : "Lib")}", isBody: false, "project reference");
    }

    /// <summary>A constant every project inherits from <c>Directory.Build.props</c>.</summary>
    private void ToggleProps()
    {
        _propsFeature = !_propsFeature;
        WriteProps();
        _touched.Add(PropsPath);
        Note($"Directory.Build.props FEATURE is now {_propsFeature}", isBody: false, "Directory.Build.props");
    }

    /// <summary>The same bytes written again: a new time, no new content.</summary>
    private void Touch()
    {
        var file = AnyFile();
        Write(file);
        File.SetLastWriteTimeUtc(PathOf(file), DateTime.UtcNow.AddSeconds(_random.Next(1, 5)));
        Count("touch");
        History.Add($"touched {file.FileName}");
    }

    private sealed record ProjectModel(string Name, string[] References)
    {
        public string[] References { get; set; } = References;
        public bool Feature { get; set; }
        public string? LinkedFile { get; set; }
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
        public List<MethodModel> Methods { get; private init; } = [];
        public bool IsPartial { get; set; }
        public string? Base { get; set; }
        public string? Statements { get; set; }
        public string Comment { get; set; } = string.Empty;
        public int ConstantValue { get; set; } = 1;
        public bool DeclaresHook { get; set; }
        public string? HookBody { get; set; }

        public bool CanDelete => Statements is null && FileName != "Shared";
        public bool CanRename => Statements is null && !IsPartial;

        public SourceFile MovedTo(string directory, string fileName) => new(directory, fileName, TypeName)
        {
            Methods = Methods,
            IsPartial = IsPartial,
            Base = Base,
            Comment = Comment,
            ConstantValue = ConstantValue,
            DeclaresHook = DeclaresHook,
            HookBody = HookBody,
        };

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
