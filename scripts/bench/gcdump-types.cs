#!/usr/bin/env -S dotnet run --file
// Aggregates a .gcdump by type: exact total bytes and object count per type, largest first.
// `dotnet-gcdump report` prints one sample object's size per row (not a per-type total), so
// its rows cannot be summed for variable-size types such as strings and arrays.
// Used by scripts/bench/measure-memory.sh --heap.
// Usage: GCDUMP_DLL=<dotnet-gcdump tool dir>/dotnet-gcdump.dll \
//          dotnet run --file scripts/bench/gcdump-types.cs -- <file.gcdump> [top=15]
// It compiles against the graph reader inside dotnet-gcdump.dll (GCHeapDump, MemoryGraph).
// Output: "total <bytes> <objects>", then "<bytes>\t<count>\t<type>" per type.
#:property AdditionalExplicitAssemblyReferences=$(GCDUMP_DLL)
#:property PublishAot=false
#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Text.RegularExpressions;
using Graphs;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: gcdump-types.cs <file.gcdump> [top]");
    return 2;
}

int top = args.Length > 1 ? int.Parse(args[1]) : 15;
var dump = new GCHeapDump(args[0]);
MemoryGraph graph = dump.MemoryGraph;
double sizeScale = dump.AverageSizeMultiplier > 0 ? dump.AverageSizeMultiplier : 1;
double countScale = dump.AverageCountMultiplier > 0 ? dump.AverageCountMultiplier : 1;

// The heap graph splits large objects into pseudo types such as "System.Byte[] (Bytes > 1M)";
// fold them back into one row per type.
var bucket = new Regex(@" \(Bytes > [^)]*\)$", RegexOptions.Compiled);
var byTypeIndex = new Dictionary<int, (long Bytes, long Count)>();
Node nodeStorage = graph.AllocNodeStorage();
long totalBytes = 0, totalCount = 0;
for (NodeIndex i = 0; i < graph.NodeIndexLimit; i++)
{
    Node node = graph.GetNode(i, nodeStorage);
    int size = node.Size;
    if (size <= 0)
    {
        continue;
    }

    int typeIndex = (int)node.TypeIndex;
    byTypeIndex.TryGetValue(typeIndex, out var entry);
    byTypeIndex[typeIndex] = (entry.Bytes + size, entry.Count + 1);
    totalBytes += size;
    totalCount++;
}

NodeType typeStorage = graph.AllocTypeNodeStorage();
var byName = new Dictionary<string, (long Bytes, long Count)>();
foreach (var (typeIndex, value) in byTypeIndex)
{
    string name = bucket.Replace(graph.GetType((NodeTypeIndex)typeIndex, typeStorage).Name, string.Empty);
    byName.TryGetValue(name, out var entry);
    byName[name] = (entry.Bytes + value.Bytes, entry.Count + value.Count);
}

Console.WriteLine($"total\t{(long)(totalBytes * sizeScale)}\t{(long)(totalCount * countScale)}");
foreach (var (name, value) in byName.OrderByDescending(p => p.Value.Bytes).Take(top))
{
    Console.WriteLine($"{(long)(value.Bytes * sizeScale)}\t{(long)(value.Count * countScale)}\t{name}");
}

return 0;
