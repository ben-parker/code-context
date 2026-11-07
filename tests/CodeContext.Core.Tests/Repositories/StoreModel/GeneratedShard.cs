using System;
using System.Collections.Generic;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

/// <summary>A deterministic jellyfin-shaped shard: 1,300 files of 20 nodes, about 11 edges a node.</summary>
internal sealed class GeneratedShard
{
    public const string Parser = "csharp";
    public const string Workspace = "default";
    public const int Files = 1300;
    public const int NodesPerFile = 20;

    public required List<CodeNode> Nodes { get; init; }
    public required List<CodeEdge> Edges { get; init; }
    public required List<string> FilePaths { get; init; }
    public required Dictionary<string, List<CodeNode>> NodesByFile { get; init; }
    public required Dictionary<string, List<CodeEdge>> EdgesByFile { get; init; }

    public static GeneratedShard Create(int seed)
    {
        var random = new Random(seed);
        var nodes = new List<CodeNode>(Files * NodesPerFile);
        var filePaths = new List<string>(Files);
        var nodesByFile = new Dictionary<string, List<CodeNode>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, string> { ["parserId"] = Parser, ["workspaceId"] = Workspace };
        string[] areas = ["MediaBrowser.Controller.Entities", "Jellyfin.Server.Implementations.Users",
            "MediaBrowser.Model.Configuration", "Emby.Server.Implementations.Library"];

        for (var f = 0; f < Files; f++)
        {
            var ns = $"{areas[f % areas.Length]}.Area{f / 40:D2}";
            var type = $"Type{f:D4}Service";
            var path = $"C:/src/jellyfin/{ns.Replace('.', '/')}/{type}.cs";
            filePaths.Add(path);
            var hash = ((uint)random.Next()).ToString("x8");
            var bucket = new List<CodeNode>(NodesPerFile);
            for (var n = 0; n < NodesPerFile; n++)
            {
                var member = n == 0 ? type : $"{type}.Method{n:D2}Async(CancellationToken,String)";
                var display = $"{ns}.{member}";
                var node = new CodeNode
                {
                    Id = $"{Parser}:{Workspace}:{hash}:{display}",
                    Identifier = $"{Parser}:{display}",
                    Name = n == 0 ? type : $"Method{n:D2}Async",
                    Type = n == 0 ? "Class" : "Method",
                    FilePath = path,
                    StartLine = n * 10,
                    EndLine = n * 10 + 8,
                    Namespace = ns,
                    Signature = $"public Task Method{n:D2}Async(CancellationToken cancellationToken, string value)",
                    Language = "csharp",
                    Metadata = meta,
                };
                nodes.Add(node);
                bucket.Add(node);
            }
            nodesByFile[path] = bucket;
        }

        // About 11.4 edges a node, sourced in the node's file; targets skewed toward a
        // popular few so some by-target buckets are large, as in a real code base.
        var edges = new List<CodeEdge>(296_000);
        var edgesByFile = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
        string[] kinds = ["CALLS", "REFERENCES", "HAS_METHOD", "IMPLEMENTS"];
        foreach (var path in filePaths)
        {
            var bucket = new List<CodeEdge>(240);
            foreach (var source in nodesByFile[path])
            {
                var count = 9 + random.Next(6);
                for (var k = 0; k < count; k++)
                {
                    var targetIndex = random.Next(4) == 0
                        ? random.Next(200)
                        : random.Next(nodes.Count);
                    var target = nodes[targetIndex];
                    var kind = kinds[random.Next(kinds.Length)];
                    var line = (source.StartLine + k).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var edge = new CodeEdge
                    {
                        Id = $"{source.Id}|{kind}|{target.Id}#{line}",
                        SourceId = source.Id,
                        TargetId = target.Id,
                        Type = kind,
                        Metadata = new Dictionary<string, string>
                        {
                            ["parserId"] = Parser, ["workspaceId"] = Workspace, ["line"] = line,
                        },
                    };
                    edges.Add(edge);
                    bucket.Add(edge);
                }
            }
            edgesByFile[path] = bucket;
        }

        return new GeneratedShard
        {
            Nodes = nodes, Edges = edges, FilePaths = filePaths,
            NodesByFile = nodesByFile, EdgesByFile = edgesByFile,
        };
    }
}
