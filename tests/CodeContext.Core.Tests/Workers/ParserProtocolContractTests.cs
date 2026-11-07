using System.Text.Json;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The persistent-state additions to the protocol (plan phase 5): every new field round-trips through the
/// source-generated context, is omitted when null (or false, for the capability), reads as absent from a
/// message without it, and the schema's definitions name exactly the fields the DTOs write.
/// </summary>
public class ParserProtocolContractTests
{
    private static readonly IndexBaseline Baseline = new(
        "C:\\repo\\.codecontext\\cache\\default\\worker-csharp.0000000042.5d0c9a1e.state",
        "5d0c9a1e7b3f4c0e9a6d2f8b1c4e7a90", new string('9', 64), [new UnitFingerprint("3fa9c1d2", "c2a4")]);

    private static JsonElement Schema()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol", "parser-protocol.schema.json")));
        return document.RootElement.Clone();
    }

    private static HashSet<string> SchemaProperties(string definition)
        => Schema().GetProperty("$defs").GetProperty(definition).GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> JsonNames(JsonElement element)
        => element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void InitializeResult_WithoutPersistentState_ReadsAsFalse_AndFalseIsNotWritten()
    {
        // A whole initialize result as a 0.10.x worker (the bundled TypeScript worker) sends it.
        var initialize = JsonSerializer.Deserialize("""
            {"parserId":"typescript","parserVersion":"0.10.4","displayName":"TypeScript/JavaScript Parser","protocolVersion":1,
             "languages":["typescript","javascript"],"extensions":[".ts",".tsx",".js",".jsx"],"projectMarkers":["tsconfig.json"],
             "capabilities":{"workspaceAnalysis":true,"incrementalUpdates":true,"semanticAnalysis":true,"nativeSyntaxTree":true},
             "spanSemantics":{"lineBase":1,"columnBase":1,"endIsInclusive":false}}
            """, ParserProtocolJsonContext.Default.InitializeResult)!;
        Assert.Equal("typescript", initialize.ParserId);
        Assert.True(initialize.Capabilities.NativeSyntaxTree);
        Assert.False(initialize.Capabilities.PersistentState);

        var json = """{"workspaceAnalysis":true,"incrementalUpdates":true,"semanticAnalysis":true,"nativeSyntaxTree":false}""";
        var capabilities = JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.WorkerCapabilities)!;
        Assert.False(capabilities.PersistentState);
        // What a worker without the capability writes is what it always wrote.
        Assert.Equal(json, JsonSerializer.Serialize(capabilities, ParserProtocolJsonContext.Default.WorkerCapabilities));

        var declared = capabilities with { PersistentState = true };
        var written = JsonSerializer.Serialize(declared, ParserProtocolJsonContext.Default.WorkerCapabilities);
        Assert.Contains("\"persistentState\":true", written);
        Assert.True(JsonSerializer.Deserialize(written, ParserProtocolJsonContext.Default.WorkerCapabilities)!.PersistentState);
    }

    [Fact]
    public void IndexWorkspaceParams_Baseline_RoundTrips_AndIsOmittedWhenNull()
    {
        var withBaseline = new IndexWorkspaceParams("default", 1, ["C:\\repo\\src\\A.cs"], Baseline);
        var json = JsonSerializer.Serialize(withBaseline, ParserProtocolJsonContext.Default.IndexWorkspaceParams);
        var back = JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.IndexWorkspaceParams)!;
        Assert.Equal(Baseline.StatePath, back.Baseline!.StatePath);
        Assert.Equal(Baseline.StateId, back.Baseline.StateId);
        Assert.Equal(Baseline.StateSha256, back.Baseline.StateSha256);
        Assert.Equal(Baseline.Fingerprints, back.Baseline.Fingerprints);

        var without = JsonSerializer.Serialize(withBaseline with { Baseline = null }, ParserProtocolJsonContext.Default.IndexWorkspaceParams);
        Assert.DoesNotContain("baseline", without);
        Assert.Null(JsonSerializer.Deserialize(without, ParserProtocolJsonContext.Default.IndexWorkspaceParams)!.Baseline);
    }

    [Fact]
    public void IndexWorkspaceResult_BaselineAnswer_RoundTrips_AndIsOmittedWhenNull()
    {
        var rejected = new IndexWorkspaceResult("default", 1, 14, true, BaselineOutcomes.Rejected, "state file is missing");
        var json = JsonSerializer.Serialize(rejected, ParserProtocolJsonContext.Default.IndexWorkspaceResult);
        Assert.Equal(rejected, JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.IndexWorkspaceResult));

        var plain = JsonSerializer.Serialize(new IndexWorkspaceResult("default", 1, 1, true), ParserProtocolJsonContext.Default.IndexWorkspaceResult);
        Assert.Equal("""{"workspaceId":"default","generation":1,"deltasEmitted":1,"complete":true}""", plain);
    }

    [Fact]
    public void AnalysisDelta_Fingerprints_RoundTrip_AndAreOmittedWhenNull()
    {
        var delta = new AnalysisDelta("csharp", "1", "default", 1, 1, false, [], [], [], true, Fingerprints: [new("<workspace>", "07be")]);
        var json = JsonSerializer.Serialize(delta, ParserProtocolJsonContext.Default.AnalysisDelta);
        Assert.Equal("07be", JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.AnalysisDelta)!.Fingerprints!.Single().Value);
        Assert.DoesNotContain("fingerprints",
            JsonSerializer.Serialize(delta with { Fingerprints = null }, ParserProtocolJsonContext.Default.AnalysisDelta));
    }

    [Fact]
    public void SaveState_RoundTrips_AndOptionalResultFieldsAreOmittedWhenNull()
    {
        var parameters = new SaveStateParams("default", Baseline.StatePath, Baseline.StateId);
        Assert.Equal(parameters, JsonSerializer.Deserialize(
            JsonSerializer.Serialize(parameters, ParserProtocolJsonContext.Default.SaveStateParams), ParserProtocolJsonContext.Default.SaveStateParams));

        var saved = new SaveStateResult("default", Baseline.StateId, true, 13400211, new string('a', 64));
        Assert.Equal(saved, JsonSerializer.Deserialize(
            JsonSerializer.Serialize(saved, ParserProtocolJsonContext.Default.SaveStateResult), ParserProtocolJsonContext.Default.SaveStateResult));
        Assert.Equal("""{"workspaceId":"default","stateId":"x","saved":false}""",
            JsonSerializer.Serialize(new SaveStateResult("default", "x", false), ParserProtocolJsonContext.Default.SaveStateResult));
        Assert.Equal("workspace/saveState", ParserProtocolMethods.SaveState);
        Assert.Equal(1, ParserProtocol.Version);
    }

    [Fact]
    public void SchemaAndDtos_NameTheSameFields_ForTheNewDefinitions()
    {
        JsonElement Written<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
            => JsonSerializer.SerializeToElement(value, info);

        Assert.Equal(SchemaProperties("indexBaseline"), JsonNames(Written(Baseline, ParserProtocolJsonContext.Default.IndexBaseline)));
        Assert.Equal(SchemaProperties("unitFingerprint"),
            JsonNames(Written(new UnitFingerprint("u", "v"), ParserProtocolJsonContext.Default.UnitFingerprint)));
        Assert.Equal(SchemaProperties("saveStateParams"),
            JsonNames(Written(new SaveStateParams("w", "p", "i"), ParserProtocolJsonContext.Default.SaveStateParams)));
        Assert.Equal(SchemaProperties("saveStateResult"),
            JsonNames(Written(new SaveStateResult("w", "i", true, 1, "s"), ParserProtocolJsonContext.Default.SaveStateResult)));
        Assert.Equal(SchemaProperties("indexWorkspaceParams"),
            JsonNames(Written(new IndexWorkspaceParams("w", 1, [], Baseline), ParserProtocolJsonContext.Default.IndexWorkspaceParams)));
        Assert.Equal(SchemaProperties("indexWorkspaceResult"),
            JsonNames(Written(new IndexWorkspaceResult("w", 1, 1, true, "accepted", "r"), ParserProtocolJsonContext.Default.IndexWorkspaceResult)));
        Assert.Equal(SchemaProperties("applyChangesResult"),
            JsonNames(Written(new ApplyChangesResult("w", 1, 1, true), ParserProtocolJsonContext.Default.ApplyChangesResult)));
        var capabilities = Schema().GetProperty("$defs").GetProperty("initializeResult").GetProperty("properties")
            .GetProperty("capabilities").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(capabilities, JsonNames(Written(new WorkerCapabilities(true, true, true, true, true), ParserProtocolJsonContext.Default.WorkerCapabilities)));

        var deltaFields = SchemaProperties("analysisDelta");
        var delta = Written(new AnalysisDelta("p", "1", "w", 1, 1, true, [], [], [], true, [], [], [], [new("u", "v")]),
            ParserProtocolJsonContext.Default.AnalysisDelta);
        Assert.Equal(deltaFields, JsonNames(delta));

        var methods = Schema().GetProperty("methods");
        Assert.Equal("#/$defs/saveStateParams", methods.GetProperty(ParserProtocolMethods.SaveState).GetProperty("params").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/indexBaseline", Schema().GetProperty("$defs").GetProperty("indexWorkspaceParams")
            .GetProperty("properties").GetProperty("baseline").GetProperty("$ref").GetString());
        var outcomes = Schema().GetProperty("$defs").GetProperty("indexWorkspaceResult").GetProperty("properties")
            .GetProperty("baseline").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Equal([BaselineOutcomes.Accepted, BaselineOutcomes.Rejected], outcomes);
    }
}
