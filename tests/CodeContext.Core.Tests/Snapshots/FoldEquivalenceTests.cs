using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The name index folds with <see cref="string.ToUpperInvariant"/> while the heap predicate compares
/// <see cref="StringComparison.OrdinalIgnoreCase"/>. The index must return a superset of what the
/// predicate accepts (plan section 3.3): <c>Equals(a, b, OrdinalIgnoreCase)</c> implies
/// <c>a.ToUpperInvariant() == b.ToUpperInvariant()</c>, and an ordinal-ignore-case substring is a
/// substring of the folded forms. Checked exhaustively over single scalar values and by property
/// over random strings, under the suite's invariant globalization (the published host's mode).
/// </summary>
public class FoldEquivalenceTests
{
    [Fact]
    public void EveryScalarValue_FoldsLikeOrdinalIgnoreCase()
    {
        var byHash = new Dictionary<int, List<string>>();
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (scalar is >= 0xD800 and <= 0xDFFF) continue;
            var text = char.ConvertFromUtf32(scalar);
            var folded = text.ToUpperInvariant();
            Assert.True(string.Equals(text, folded, StringComparison.OrdinalIgnoreCase), $"U+{scalar:X4} is not OrdinalIgnoreCase-equal to its fold");
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(text);
            if (!byHash.TryGetValue(hash, out var bucket)) byHash[hash] = bucket = new List<string>(1);
            bucket.Add(text);
        }

        // OrdinalIgnoreCase-equal strings share a hash, so every equal pair is inside one bucket.
        foreach (var bucket in byHash.Values.Where(bucket => bucket.Count > 1))
            for (var i = 0; i < bucket.Count; i++)
                for (var j = i + 1; j < bucket.Count; j++)
                    if (string.Equals(bucket[i], bucket[j], StringComparison.OrdinalIgnoreCase))
                        Assert.True(bucket[i].ToUpperInvariant() == bucket[j].ToUpperInvariant(),
                            $"U+{char.ConvertToUtf32(bucket[i], 0):X4} and U+{char.ConvertToUtf32(bucket[j], 0):X4} are equal ignoring case but fold apart");
    }

    [Fact]
    public void RandomStrings_EqualOrContainedIgnoringCase_AreSoWhenFolded()
    {
        var random = new Random(33);
        for (var round = 0; round < 20_000; round++)
        {
            var a = RandomText(random, random.Next(0, 12));
            var b = Recase(random, a);
            Assert.Equal(0, string.Compare(a, b, StringComparison.OrdinalIgnoreCase));   // ordinal, not xunit's own ignore-case
            Assert.Equal(a.ToUpperInvariant(), b.ToUpperInvariant());

            if (a.Length == 0) continue;
            var start = random.Next(a.Length);
            if (char.IsLowSurrogate(a[start])) start--;
            var end = random.Next(start, a.Length + 1);
            if (end < a.Length && char.IsLowSurrogate(a[end])) end++;
            var needle = Recase(random, a[start..end]);
            var haystack = Recase(random, a);
            Assert.True(haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(needle.ToUpperInvariant(), haystack.ToUpperInvariant(), StringComparison.Ordinal);
        }
    }

    private static string Recase(Random random, string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            builder.Append((random.Next(3) switch { 0 => Rune.ToUpperInvariant(rune), 1 => Rune.ToLowerInvariant(rune), _ => rune }).ToString());
        return builder.ToString();
    }

    private static string RandomText(Random random, int length)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            int scalar;
            do
            {
                scalar = random.Next(4) switch
                {
                    0 => random.Next(0x20, 0x7F),
                    1 => random.Next(0xA0, 0x2000),
                    2 => random.Next(0x10400, 0x10500),   // Deseret and others with case
                    _ => random.Next(0, 0x110000),
                };
            }
            while (scalar is >= 0xD800 and <= 0xDFFF);
            builder.Append(char.ConvertFromUtf32(scalar));
        }
        return builder.ToString();
    }
}
