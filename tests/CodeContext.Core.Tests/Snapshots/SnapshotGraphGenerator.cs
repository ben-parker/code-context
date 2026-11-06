using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Seeded random graphs for snapshot round trips, committed the way workers commit them. Three
/// owned shards (one with a workspace id that needs escaping); every field null, empty or set,
/// non-ASCII and supplementary characters, case and fold collisions in names, metadata sets with
/// canonical and non-canonical <c>line</c>/<c>column</c>, dangling and cross-shard targets, null
/// targets, the edge id shapes of the C# worker (<c>S=[K]=&gt;T@L:C</c>), the TypeScript worker
/// (<c>S=[K]=&gt;T</c> with an optional suffix) and the fake worker (<c>S-&gt;contains-&gt;T</c>), and
/// verbatim ids that do not decompose.
/// </summary>
internal sealed class SnapshotGraphGenerator
{
    public static readonly ShardKey[] Shards =
    [
        new("csharp", "w 1/\u00FC"), new("fake", "ws"), new("typescript", "default"),
    ];

    private static readonly string[] NamePool =
    [
        "Widget", "WIDGET", "widget", "WidgetTests", "stra\u00DFe", "STRASSE", "\u01C5ungla", "\u01C4UNGLA", "\uFB01le",
        "\u0130stanbul", "istanbul", "\u0131", "I", "\u2126mega", "\u03A9MEGA", "\U00010400x", "\U00010428X", "caf\u00E9",
        "Cafe\u0301", "\u4E2D\u6587", "Alpha", "alpha.beta", "a", "",
    ];

    private static readonly string?[] PathPool =
    [
        null, "", "   ", "/repo/src/A.cs", "/repo/src/a.cs", "/repo/SRC/A.cs", "C:\\repo\\src\\B.cs", "C:/repo/src/B.cs",
        "/repo/tests/ATests.cs", "/repo/src/\u00FCber/C.cs", "external:System.Runtime/System.String", "relative/D.cs",
        "/repo/src/A.cs/", "/repo/\U00010400/E.cs",
    ];

    private static readonly string?[] TypePool = [null, "", "Class", "Method", "class", "Property", "\u00C9num"];

    private static readonly string?[] KindPool = [null, "CALLS", "REFERENCES", "contains", "calls", "IMPLEMENTS"];

    private static readonly string?[] PositionPool =
    [
        "12", "-3", "0", "2147483647", "-2147483647", "007", "+5", "-0", "", "abc", " 1", "2147483648", "-2147483648", null,
    ];

    private readonly Random _random;
    private int _counter;

    public SnapshotGraphGenerator(int seed) => _random = new Random(seed);

    public static string Prefix(ShardKey key) => $"{key.ParserId}:{Uri.EscapeDataString(key.WorkspaceId)}:";

    public (ShardKey Key, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges)[] Generate(int maxNodes = 60, int maxEdges = 160)
    {
        var nodeIdsByShard = new Dictionary<ShardKey, List<string>>();
        var nodesByShard = new Dictionary<ShardKey, List<CodeNode>>();
        foreach (var key in Shards)
        {
            var nodes = new List<CodeNode>();
            for (var i = _random.Next(maxNodes + 1); i > 0; i--) nodes.Add(MakeNode(key));
            nodesByShard[key] = nodes;
            nodeIdsByShard[key] = nodes.Select(node => node.Id!).ToList();
        }

        var result = new List<(ShardKey, IReadOnlyList<CodeNode>, IReadOnlyList<CodeEdge>)>();
        foreach (var key in Shards)
        {
            var edges = new List<CodeEdge>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = _random.Next(maxEdges + 1); i > 0; i--)
            {
                var edge = MakeEdge(key, nodeIdsByShard);
                if (seen.Add(edge.Id!)) edges.Add(edge);
            }
            result.Add((key, nodesByShard[key], edges));
        }
        return result.ToArray();
    }

    private CodeNode MakeNode(ShardKey key)
    {
        var k = ++_counter;
        var id = Prefix(key) + Pick(["Type", "M\u00E9thode", "\U00010400", "x"]) + "." + k.ToString(CultureInfo.InvariantCulture);
        var metadata = SnapshotTestSupport.Owned(key);
        for (var i = _random.Next(3); i > 0; i--) metadata[Pick(["kind", "isTest", "assembly", "\u00E9", ""])] = RandomText();
        if (_random.Next(4) == 0) metadata["external"] = _random.Next(3) == 0 ? "false" : "true";
        return new CodeNode
        {
            Id = id,
            Identifier = _random.Next(8) switch
            {
                0 => string.Empty,
                1 => "   ",
                _ => $"ident:{key.ParserId}:{k}{Pick(["", "\u00FC", "#\U00010400"])}",
            },
            Name = _random.Next(10) == 0 ? null : Pick(NamePool),
            Type = Pick(TypePool),
            Language = Maybe(Pick(["csharp", "typescript"])),
            FilePath = Pick(PathPool),
            StartLine = Number(),
            EndLine = Number(),
            StartCol = Number(),
            EndCol = Number(),
            Namespace = Maybe("Ns." + RandomText()),
            Visibility = Maybe(Pick(["public", "internal", ""])),
            Signature = Maybe("void M(" + RandomText() + ")"),
            ReturnType = Maybe(RandomText()),
            Parameters = Maybe(RandomText()),
            Modifiers = Maybe(Pick(["static", "async static", ""])),
            Metrics = Maybe("{\"loc\":" + _random.Next(100) + "}"),
            Metadata = metadata,
        };
    }

    private CodeEdge MakeEdge(ShardKey key, Dictionary<ShardKey, List<string>> nodeIds)
    {
        var own = nodeIds[key];
        var source = own.Count > 0 && _random.Next(6) != 0
            ? Pick(own)
            : Prefix(key) + "dangling-source." + _random.Next(5);
        var targetRoll = _random.Next(10);
        var other = Pick(Shards);
        string? target = targetRoll switch
        {
            0 => null,
            1 => "missing:" + _random.Next(5),
            2 when nodeIds[other].Count > 0 => Pick(nodeIds[other]),
            _ when own.Count > 0 => Pick(own),
            _ => Prefix(key) + "dangling-target",
        };
        var kind = Pick(KindPool);
        var metadata = SnapshotTestSupport.Owned(key);
        var line = Pick(PositionPool);
        var column = Pick(PositionPool);
        if (line is not null) metadata["line"] = line;
        if (column is not null) metadata["column"] = column;
        if (_random.Next(5) == 0) metadata[Pick(["resolved", "via", "\u00E9"])] = RandomText();

        var shapeRoll = _random.Next(5);
        var id = (shapeRoll, target) switch
        {
            (0, not null) => $"{source}=[{kind}]=>{target}@{line}:{column}",
            (1, not null) => $"{source}=[{kind}]=>{target}" + (_random.Next(2) == 0 ? "" : $"@{line}:{column ?? "0"}"),
            (2, not null) => $"{source}->contains->{target}",
            (3, _) => $"{Prefix(key)}verbatim:{++_counter}",
            _ => $"{source}=[{kind}]=>{target}#{++_counter}",
        };
        return new CodeEdge { Id = id, SourceId = source, TargetId = target, Type = kind, Metadata = metadata };
    }

    private int Number() => _random.Next(8) switch
    {
        0 => int.MinValue,
        1 => int.MaxValue,
        2 => -_random.Next(100),
        _ => _random.Next(5000),
    };

    private string? Maybe(string value) => _random.Next(5) switch
    {
        0 => null,
        1 => string.Empty,
        _ => value,
    };

    private string RandomText()
    {
        var length = _random.Next(0, 8);
        var chars = new List<char>();
        for (var i = 0; i < length; i++)
        {
            switch (_random.Next(6))
            {
                case 0: chars.Add((char)_random.Next(0x20, 0x7F)); break;
                case 1: chars.Add((char)_random.Next(0xA0, 0x800)); break;
                case 2: chars.Add((char)_random.Next(0x3000, 0xD7FF)); break;
                case 3: chars.AddRange(char.ConvertFromUtf32(_random.Next(0x10000, 0x10FFFF)).ToCharArray()); break;
                default: chars.Add((char)_random.Next('a', 'z' + 1)); break;
            }
        }
        return new string(chars.ToArray());
    }

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];
}
