using CodeContext.Parser.Protocol;

namespace CodeContext.CSharp.Worker.State;

/// <summary>
/// One value per unit covering everything its walked facts depend on, so a unit whose value is
/// unchanged walks to the same facts and a restored worker may keep its stored walks:
///
/// <code>
/// fingerprint(U) = SHA-256( "ccfp1", workerIdentity, unitKey, Signature(U), runtimeReferences(U),
///     for each file U compiles, by path:   path, contentSha256, ownerUnitKey,
///     for each unit U references, by key:  key, fingerprint(R) )
/// </code>
///
/// A unit lists what it references transitively and each fingerprint includes those of its
/// references, so the units whose value moved are closed under "depends on". Coverage is not an
/// input: it is recomputed from the project model and the walk, and changes no fact.
/// </summary>
internal static class UnitFingerprints
{
    public const string Scheme = "ccfp1";

    /// <param name="Key">The unit's key: the project key, or the fallback's qualifier.</param>
    /// <param name="Signature">What the project read as (see the analyzer's <c>Signature</c>); the fallback's usings.</param>
    /// <param name="BindsAgainstRuntime">Whether the compilation carries the worker runtime's own references.</param>
    /// <param name="References">Keys of the units whose compilations this one references.</param>
    public sealed record UnitInput(
        string Key,
        string Signature,
        bool BindsAgainstRuntime,
        IReadOnlyList<FileInput> Files,
        IReadOnlyList<string> References);

    /// <param name="ContentHash">SHA-256 of the file's bytes as last read.</param>
    /// <param name="OwnerKey">The unit whose ids the file's symbols carry.</param>
    public readonly record struct FileInput(string Path, byte[] ContentHash, string OwnerKey);

    /// <summary>
    /// The fingerprints of <paramref name="units"/>, given dependencies before dependents, in key
    /// order. A reference to a unit not listed before its dependent is a caller bug.
    /// </summary>
    public static List<UnitFingerprint> Compute(string workerIdentity, IReadOnlyList<UnitInput> units)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var hasher = new CSharpWorkspaceAnalyzer.FactHasher();
        foreach (var unit in units)
        {
            hasher.Str(Scheme);
            hasher.Str(workerIdentity);
            hasher.Str(unit.Key);
            hasher.Str(unit.Signature);
            hasher.Int(unit.BindsAgainstRuntime ? 1 : 0);

            hasher.Int(unit.Files.Count);
            foreach (var file in unit.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                hasher.Str(file.Path);
                hasher.Bytes(file.ContentHash);
                hasher.Str(file.OwnerKey);
            }

            var references = unit.References.Order(StringComparer.Ordinal).ToList();
            hasher.Int(references.Count);
            foreach (var reference in references)
            {
                if (!values.TryGetValue(reference, out var referenced))
                {
                    throw new InvalidOperationException($"Unit '{unit.Key}' references '{reference}' before it is fingerprinted.");
                }
                hasher.Str(reference);
                hasher.Str(referenced);
            }

            values[unit.Key] = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }

        return values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UnitFingerprint(pair.Key, pair.Value))
            .ToList();
    }
}
