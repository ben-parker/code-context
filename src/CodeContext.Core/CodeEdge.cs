namespace CodeContext.Core
{
    public class CodeEdge
    {
        // The id as set (a string), or as the store keeps it: an EdgeId whose parts are the endpoint ids and
        // two shared pieces, which reproduce the text exactly (EdgeId.Compose). Setting Id always stores the
        // string given; the parts never change with SourceId or TargetId.
        private object? _id;

        /// <summary>The edge's id. For an edge the store keeps as parts, each read produces the text.</summary>
        public string? Id
        {
            get => _id as string ?? (_id as EdgeId)?.ToString();
            set => _id = value;
        }

        public string? SourceId { get; set; }
        public string? TargetId { get; set; }
        public string? Type { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// The id as the store keys it: the string set, or the <see cref="EdgeId"/> of its parts (null when there
        /// is no id). Compare and hash it with <see cref="EdgeIdKeys"/>, never by reference.
        /// </summary>
        internal object? IdKey => _id;

        /// <summary>Keeps <paramref name="id"/>, the parts of this edge's id text, in place of the text.</summary>
        internal void UseIdKey(object? id) => _id = id;
    }
}
