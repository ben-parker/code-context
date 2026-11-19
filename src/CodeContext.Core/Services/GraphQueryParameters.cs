using System.Globalization;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// The parameters each graph query takes, by the name REST and MCP use. An option that belongs to another query, or
    /// to none, is refused with a message saying so rather than ignored: a caller who passes <c>within</c> to a path
    /// query is told it applies only to hubs, not handed an unfiltered answer.
    /// </summary>
    public static class GraphQueryParameters
    {
        public enum Kind { String, Integer, Boolean }

        public sealed record Parameter(string Name, Kind Kind, bool Required = false);

        public const string PathQuery = "path";
        public const string AffectedQuery = "affected";
        public const string HubsQuery = "hubs";

        public static readonly IReadOnlyList<Parameter> Path =
        [
            new("from", Kind.String, Required: true),
            new("to", Kind.String, Required: true),
            new("maxDepth", Kind.Integer),
            new("relation", Kind.String),
            new("includeTests", Kind.Boolean),
        ];

        public static readonly IReadOnlyList<Parameter> Affected =
        [
            new("identifier", Kind.String, Required: true),
            new("maxDepth", Kind.Integer),
            new("relation", Kind.String),
            new("includeTests", Kind.Boolean),
            new("limit", Kind.Integer),
        ];

        public static readonly IReadOnlyList<Parameter> Hubs =
        [
            new("limit", Kind.Integer),
            new("relation", Kind.String),
            new("type", Kind.String),
            new("within", Kind.String),
            new("includeTests", Kind.Boolean),
            new("includeExternal", Kind.Boolean),
        ];

        public static IReadOnlyList<Parameter> For(string query) => query switch
        {
            PathQuery => Path,
            AffectedQuery => Affected,
            HubsQuery => Hubs,
            _ => throw new ArgumentOutOfRangeException(nameof(query), query, "Unknown graph query."),
        };

        /// <summary>The parameter <paramref name="name"/> of <paramref name="query"/> (ignoring case), or null.</summary>
        public static Parameter? Find(string query, string name)
            => For(query).FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Why <paramref name="name"/> is refused by <paramref name="query"/>, naming the queries it belongs to.</summary>
        public static string NotApplicable(string query, string name)
        {
            var owners = new[] { PathQuery, AffectedQuery, HubsQuery }
                .Where(other => other != query && Find(other, name) is not null)
                .ToList();
            var belongs = owners.Count > 0
                ? $" (it is an option of {string.Join(" and ", owners.Select(owner => owner + " queries"))})"
                : string.Empty;
            return $"'{name}' does not apply to {query} queries{belongs}; {query} accepts "
                + $"{string.Join(", ", For(query).Select(parameter => parameter.Name))}.";
        }

        /// <summary>
        /// Checks raw text values (a query string): every name known to <paramref name="query"/>, given once, of its kind,
        /// required ones present and not blank. Returns the first problem, or null.
        /// </summary>
        public static string? Check(string query, IEnumerable<KeyValuePair<string, IReadOnlyList<string?>>> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, given) in values)
            {
                if (Find(query, name) is not { } parameter) return NotApplicable(query, name);
                seen.Add(parameter.Name);
                if (given.Count != 1) return $"{parameter.Name} is given {given.Count} times; give it once.";
                if (KindProblem(parameter, given[0]) is { } problem) return problem;
            }
            return For(query).FirstOrDefault(parameter => parameter.Required && !seen.Contains(parameter.Name)) is { } missing
                ? $"{missing.Name} is required."
                : null;
        }

        private static string? KindProblem(Parameter parameter, string? value)
        {
            switch (parameter.Kind)
            {
                case Kind.Integer when !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _):
                    return $"{parameter.Name} must be an integer (got '{value}').";
                case Kind.Boolean when !bool.TryParse(value, out _):
                    return $"{parameter.Name} must be true or false (got '{value}').";
                case Kind.String when parameter.Required && string.IsNullOrWhiteSpace(value):
                    return $"{parameter.Name} is required.";
                default:
                    return null;
            }
        }
    }
}
