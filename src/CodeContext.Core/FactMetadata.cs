using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace CodeContext.Core
{
    /// <summary>
    /// The metadata of a stored node or edge: an immutable, ordinal string map that enumerates in the
    /// order it was built from. One instance is shared by every fact of a shard with the same content
    /// (see <see cref="FactMetadataPool"/>), so it has no mutating member and no way back to a mutable
    /// collection. Pairs are held flat, keys at even and values at odd positions; a small map is
    /// searched in order, a larger one through an index built once.
    /// </summary>
    public sealed class FactMetadata : IReadOnlyDictionary<string, string>
    {
        private const int LinearLimit = 8;

        private readonly string[] _pairs;
        private readonly Dictionary<string, int>? _index;

        /// <summary>A map of <paramref name="pairs"/> (key, value, key, value, ...), which it takes; a repeated or null key throws.</summary>
        internal FactMetadata(string[] pairs)
        {
            ArgumentNullException.ThrowIfNull(pairs);
            if (pairs.Length % 2 != 0) throw new ArgumentException("Metadata pairs must have even length.", nameof(pairs));
            _pairs = pairs;
            var count = pairs.Length / 2;
            if (count > LinearLimit)
            {
                _index = new Dictionary<string, int>(count, StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var key = pairs[2 * i] ?? throw new ArgumentException("A metadata key is null.", nameof(pairs));
                    if (!_index.TryAdd(key, 2 * i + 1)) throw new ArgumentException($"Metadata key '{key}' appears twice.", nameof(pairs));
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var key = pairs[2 * i] ?? throw new ArgumentException("A metadata key is null.", nameof(pairs));
                    for (var j = 0; j < i; j++)
                    {
                        if (string.Equals(pairs[2 * j], key, StringComparison.Ordinal))
                            throw new ArgumentException($"Metadata key '{key}' appears twice.", nameof(pairs));
                    }
                }
            }
        }

        public int Count => _pairs.Length / 2;

        public string this[string key]
            => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The metadata key '{key}' was not present.");

        public IEnumerable<string> Keys
        {
            get
            {
                for (var i = 0; i < _pairs.Length; i += 2) yield return _pairs[i];
            }
        }

        public IEnumerable<string> Values
        {
            get
            {
                for (var i = 1; i < _pairs.Length; i += 2) yield return _pairs[i];
            }
        }

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (_index is not null)
            {
                if (_index.TryGetValue(key, out var at))
                {
                    value = _pairs[at];
                    return true;
                }
            }
            else
            {
                for (var i = 0; i < _pairs.Length; i += 2)
                {
                    if (string.Equals(_pairs[i], key, StringComparison.Ordinal))
                    {
                        value = _pairs[i + 1];
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            for (var i = 0; i < _pairs.Length; i += 2) yield return new KeyValuePair<string, string>(_pairs[i], _pairs[i + 1]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Hands out one <see cref="FactMetadata"/> per distinct content (the same pairs in the same order),
    /// and one string instance per distinct key, value and other repeated text it is asked to intern. Used
    /// by one writer at a time over one batch of facts (a snapshot shard, a worker request); not thread-safe.
    /// </summary>
    internal sealed class FactMetadataPool
    {
        private readonly Dictionary<PairsKey, FactMetadata> _sets = new();
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        public int DistinctSets => _sets.Count;

        /// <summary>The pooled instance of <paramref name="value"/>, which becomes the pooled one when new.</summary>
        [return: NotNullIfNotNull(nameof(value))]
        public string? Intern(string? value)
        {
            if (value is null) return null;
            if (_strings.TryGetValue(value, out var pooled)) return pooled;
            _strings.Add(value, value);
            return value;
        }

        /// <summary>
        /// The shared map of <paramref name="pairs"/> (key, value, ...). The array is taken (its strings
        /// interned in place) only when the content is new; the caller must not touch it afterwards.
        /// </summary>
        public FactMetadata Get(string[] pairs)
        {
            for (var i = 0; i < pairs.Length; i++) pairs[i] = Intern(pairs[i]);
            var key = new PairsKey(pairs);
            if (_sets.TryGetValue(key, out var shared)) return shared;
            shared = new FactMetadata(pairs);
            _sets.Add(key, shared);
            return shared;
        }

        private readonly struct PairsKey(string[] pairs) : IEquatable<PairsKey>
        {
            private readonly string[] _pairs = pairs;

            public bool Equals(PairsKey other) => _pairs.AsSpan().SequenceEqual(other._pairs);

            public override bool Equals(object? obj) => obj is PairsKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                foreach (var value in _pairs) hash.Add(value is null ? 0 : StringComparer.Ordinal.GetHashCode(value));
                return hash.ToHashCode();
            }
        }
    }
}
