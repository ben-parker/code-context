using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeContext.Mcp;
using ModelContextProtocol;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Acceptance test for the Phase 3b manual (AOT-safe) MCP tool registration: the
/// <see cref="McpToolCatalog"/>'s <c>tools/list</c> output must be semantically identical to the
/// golden snapshot captured in Phase 1 (before the attribute-discovery path was removed). The
/// comparison is normalized — object key order may differ and tool order is irrelevant — but
/// tool names, descriptions, input schemas, defaults, and the execution hint must match exactly.
/// </summary>
public class McpToolCatalogTests
{
    [Fact]
    public void ListTools_MatchesGoldenSnapshot()
    {
        var actual = SerializeCatalog();
        var expected = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-tools-list.snapshot.json"));

        var actualTools = ToolsByName(actual);
        var expectedTools = ToolsByName(expected);

        Assert.Equal(
            expectedTools.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actualTools.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (name, expectedTool) in expectedTools)
        {
            Assert.True(actualTools.TryGetValue(name, out var actualTool));
            Assert.Equal(expectedTool, actualTool);
        }
    }

    private static string SerializeCatalog() =>
        JsonSerializer.Serialize(McpToolCatalog.ListTools(), McpJsonUtilities.DefaultOptions);

    private static Dictionary<string, string> ToolsByName(string json)
    {
        var tools = JsonNode.Parse(json)!.AsObject()["tools"]!.AsArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = tool!.AsObject()["name"]!.GetValue<string>();
            result[name] = Canonicalize(tool);
        }
        return result;
    }

    // Normalizes object key order (recursively) while preserving array order, so schema arrays
    // like enum:["Compact","Full"] and type:["string","null"] are still compared positionally.
    private static string Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var members = new StringBuilder("{");
                var first = true;
                foreach (var member in obj.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    if (!first) members.Append(',');
                    first = false;
                    members.Append(JsonSerializer.Serialize(member.Key)).Append(':').Append(Canonicalize(member.Value));
                }
                return members.Append('}').ToString();
            case JsonArray array:
                var items = new StringBuilder("[");
                var firstItem = true;
                foreach (var item in array)
                {
                    if (!firstItem) items.Append(',');
                    firstItem = false;
                    items.Append(Canonicalize(item));
                }
                return items.Append(']').ToString();
            case null:
                return "null";
            default:
                return node.ToJsonString();
        }
    }
}
