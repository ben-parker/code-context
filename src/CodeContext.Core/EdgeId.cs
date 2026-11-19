using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace CodeContext.Core
{
    /// <summary>
    /// An edge id the store holds as parts: <c>Source + Middle + Target + Tail</c>, where Source and Target are
    /// the edge's endpoint ids (the instances the endpoint nodes hold) and Middle and Tail are shared pieces.
    /// Edge ids are worker-owned and opaque: an id is held this way only when it is exactly that concatenation
    /// of its own endpoints, and as the string it arrived as otherwise (<see cref="Compose"/>). The id text is
    /// produced only where it is needed (<see cref="ToString"/>); equality, hashing and ordinal order work on
    /// the parts (<see cref="EdgeIdKeys"/>). Immutable.
    /// </summary>
    internal sealed class EdgeId
    {
        private EdgeId(string source, string middle, string target, string tail, bool canonical)
        {
            Source = source;
            Middle = middle;
            Target = target;
            Tail = tail;
            Canonical = canonical;
            var hasher = new EdgeIdKeys.Hasher();
            hasher.Append(source);
            hasher.Append(middle);
            hasher.Append(target);
            hasher.Append(tail);
            Hash = hasher.Finish();
        }

        public string Source { get; }

        public string Middle { get; }

        public string Target { get; }

        public string Tail { get; }

        /// <summary>The id's hash under <see cref="EdgeIdKeys.Comparer"/>, the same as its text's; computed once.</summary>
        public int Hash { get; }

        /// <summary>
        /// Whether the split is the one the snapshot writer derives from the text: Target at its first
        /// occurrence after Source. Always true for <see cref="Compose"/>; checked for <see cref="FromParts"/>.
        /// </summary>
        public bool Canonical { get; }

        public int Length => Source.Length + Middle.Length + Target.Length + Tail.Length;

        /// <summary>
        /// The key the store keeps for <paramref name="id"/>: an <see cref="EdgeId"/> when <paramref name="id"/>
        /// starts with <paramref name="source"/> and holds <paramref name="target"/> after it (first occurrence),
        /// so the parts reproduce it exactly; otherwise <paramref name="id"/> itself. The pieces between go
        /// through <paramref name="share"/>, which returns the instance to keep for a piece.
        /// </summary>
        [return: NotNullIfNotNull(nameof(id))]
        public static object? Compose(string? id, string? source, string? target, Func<ReadOnlySpan<char>, string> share)
        {
            if (id is null || source is null || target is null || !id.StartsWith(source, StringComparison.Ordinal)) return id;
            var at = id.IndexOf(target, source.Length, StringComparison.Ordinal);
            if (at < 0) return id;
            return new EdgeId(
                source,
                share(id.AsSpan(source.Length, at - source.Length)),
                target,
                share(id.AsSpan(at + target.Length)),
                canonical: true);
        }

        /// <summary>An id given as its parts (a snapshot's composed form), whatever split they use.</summary>
        public static EdgeId FromParts(string source, string middle, string target, string tail)
            => new(source, middle, target, tail, IsFirstOccurrence(middle, target));

        // Target's first occurrence in Middle + Target + Tail is at Middle.Length when none starts inside Middle;
        // one that starts there ends inside Middle + Target.
        private static bool IsFirstOccurrence(string middle, string target)
        {
            if (middle.Length == 0) return true;
            if (target.Length == 0) return false;
            var length = middle.Length + target.Length - 1;
            Span<char> buffer = length <= 512 ? stackalloc char[length] : new char[length];
            middle.AsSpan().CopyTo(buffer);
            target.AsSpan(0, target.Length - 1).CopyTo(buffer[middle.Length..]);
            return buffer.IndexOf(target.AsSpan()) < 0;
        }

        public void CopyTo(Span<char> destination)
        {
            Source.AsSpan().CopyTo(destination);
            destination = destination[Source.Length..];
            Middle.AsSpan().CopyTo(destination);
            destination = destination[Middle.Length..];
            Target.AsSpan().CopyTo(destination);
            Tail.AsSpan().CopyTo(destination[Target.Length..]);
        }

        /// <summary>The id text, allocated on each call.</summary>
        public override string ToString() => string.Create(Length, this, static (span, id) => id.CopyTo(span));

        internal ReadOnlySpan<char> Segment(int index) => index switch
        {
            0 => Source,
            1 => Middle,
            2 => Target,
            _ => Tail,
        };
    }

    /// <summary>
    /// The edge id keys the store keeps (<see cref="CodeEdge.IdKey"/>): a <see cref="string"/> or an
    /// <see cref="EdgeId"/>, equal, hashed and ordered by their text, so a key held as parts and the same id
    /// given as a string are one key.
    /// </summary>
    internal static class EdgeIdKeys
    {
        /// <summary>Equality and hashing by id text, ordinal, for keys of either kind.</summary>
        public static readonly IEqualityComparer<object> Comparer = new KeyComparer();

        /// <summary>Ordinal order of the ids' text (<see cref="string.CompareOrdinal(string, string)"/>'s sign), null first.</summary>
        public static int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            if (left is string a && right is string b) return string.CompareOrdinal(a, b);

            int leftAt = 0, rightAt = 0;
            // Composed ids of one source (a by-source bucket) share their leading parts by reference.
            if (left is EdgeId l && right is EdgeId r && ReferenceEquals(l.Source, r.Source))
            {
                leftAt = rightAt = 1;
                if (ReferenceEquals(l.Middle, r.Middle))
                {
                    leftAt = rightAt = 2;
                    if (ReferenceEquals(l.Target, r.Target)) leftAt = rightAt = 3;
                }
            }
            int leftCount = Count(left), rightCount = Count(right);
            var x = Segment(left, leftAt);
            var y = Segment(right, rightAt);
            while (true)
            {
                while (x.IsEmpty && leftAt + 1 < leftCount) x = Segment(left, ++leftAt);
                while (y.IsEmpty && rightAt + 1 < rightCount) y = Segment(right, ++rightAt);
                if (x.IsEmpty || y.IsEmpty) return x.IsEmpty ? (y.IsEmpty ? 0 : -1) : 1;
                var n = Math.Min(x.Length, y.Length);
                var order = x[..n].SequenceCompareTo(y[..n]);
                if (order != 0) return order;
                x = x[n..];
                y = y[n..];
            }
        }

        /// <summary>Whether two keys name the same id text.</summary>
        public static bool AreEqual(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            if (left is string a && right is string b) return string.Equals(a, b, StringComparison.Ordinal);
            if (Length(left) != Length(right)) return false;
            if (left is EdgeId l && right is EdgeId r)
            {
                if (l.Hash != r.Hash) return false;
                // Over the same endpoints, two canonical splits of one text are the same split. A split read from
                // elsewhere may not be canonical: the same text cut differently, which only the text comparison sees.
                if (l.Canonical && r.Canonical && ReferenceEquals(l.Source, r.Source) && ReferenceEquals(l.Target, r.Target))
                    return string.Equals(l.Middle, r.Middle, StringComparison.Ordinal) && string.Equals(l.Tail, r.Tail, StringComparison.Ordinal);
            }
            return Compare(left, right) == 0;
        }

        public static int Hash(object key) => key switch
        {
            EdgeId id => id.Hash,
            string text => Of(text),
            _ => throw new ArgumentException("An edge id key is a string or an EdgeId.", nameof(key)),
        };

        public static int Length(object key) => key is EdgeId id ? id.Length : ((string)key).Length;

        /// <summary>Whether the id text starts with <paramref name="prefix"/> (ordinal).</summary>
        public static bool StartsWith(object key, string prefix)
        {
            if (key is string text) return text.StartsWith(prefix, StringComparison.Ordinal);
            var id = (EdgeId)key;
            if (id.Length < prefix.Length) return false;
            var rest = prefix.AsSpan();
            for (var i = 0; i < 4 && !rest.IsEmpty; i++)
            {
                var segment = id.Segment(i);
                var n = Math.Min(segment.Length, rest.Length);
                if (!segment[..n].SequenceEqual(rest[..n])) return false;
                rest = rest[n..];
            }
            return true;
        }

        /// <summary>The id text written into <paramref name="destination"/>, which holds at least <see cref="Length(object)"/> chars.</summary>
        public static ReadOnlySpan<char> Text(object key, Span<char> destination)
        {
            if (key is string text) return text;
            var id = (EdgeId)key;
            id.CopyTo(destination);
            return destination[..id.Length];
        }

        private static int Count(object key) => key is EdgeId ? 4 : 1;

        private static ReadOnlySpan<char> Segment(object key, int index)
            => key is EdgeId id ? id.Segment(index) : (string)key;

        private static int Of(string text)
        {
            var hasher = new Hasher();
            hasher.Append(text);
            return hasher.Finish();
        }

        private sealed class KeyComparer : IEqualityComparer<object>
        {
            public new bool Equals(object? x, object? y) => AreEqual(x, y);

            public int GetHashCode(object obj) => Hash(obj);
        }

        /// <summary>
        /// A hash of UTF-16 text that can be fed in pieces: the same text gives the same hash however it is
        /// split. Seeded per process, so ids a repository chooses cannot be aimed at one bucket ahead of time.
        /// </summary>
        internal struct Hasher
        {
            private const ulong Prime1 = 0x9E3779B185EBCA87UL;
            private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
            private const ulong Prime3 = 0x165667B19E3779F9UL;
            private static readonly ulong Seed = (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.NextInt64() << 1);

            private ulong _state;
            private ulong _pending;
            private int _pendingChars;
            private int _length;
            private bool _started;

            public void Append(ReadOnlySpan<char> text)
            {
                if (!_started)
                {
                    _state = Seed;
                    _started = true;
                }
                _length += text.Length;
                while (_pendingChars > 0 && !text.IsEmpty)
                {
                    _pending |= (ulong)text[0] << (16 * _pendingChars);
                    text = text[1..];
                    if (++_pendingChars == 4)
                    {
                        Mix(_pending);
                        _pending = 0;
                        _pendingChars = 0;
                    }
                }
                var i = 0;
                for (; i + 4 <= text.Length; i += 4)
                    Mix(text[i] | ((ulong)text[i + 1] << 16) | ((ulong)text[i + 2] << 32) | ((ulong)text[i + 3] << 48));
                for (; i < text.Length; i++)
                    _pending |= (ulong)text[i] << (16 * _pendingChars++);
            }

            public int Finish()
            {
                if (!_started) _state = Seed;
                if (_pendingChars > 0) Mix(_pending);
                var hash = _state ^ (ulong)_length;
                hash ^= hash >> 33;
                hash *= Prime2;
                hash ^= hash >> 29;
                hash *= Prime3;
                hash ^= hash >> 32;
                return (int)hash ^ (int)(hash >> 32);
            }

            private void Mix(ulong chunk)
            {
                chunk *= Prime2;
                chunk = BitOperations.RotateLeft(chunk, 31);
                chunk *= Prime1;
                _state ^= chunk;
                _state = BitOperations.RotateLeft(_state, 27) * Prime1 + Prime3;
            }
        }
    }
}
