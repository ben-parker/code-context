
namespace CodeContext.Core
{
    public class CodeContextOptions
    {
        public const string SectionName = "CodeContext";

        public string RootPath { get; set; } = string.Empty;
        /// <summary>Random identifier for this host instance; /api/shutdown requires it.</summary>
        public string InstanceId { get; set; } = string.Empty;
        public int Port { get; set; } = 7890;
        /// <summary>Minutes without API activity before the instance shuts itself down. 0 disables.</summary>
        public int IdleTimeoutMinutes { get; set; } = 120;
        public string[] IgnorePatterns { get; set; } = ["node_modules/**", "bin/**", "obj/**", ".venv/**", ".git/**", ".codecontext/**"];

        /// <summary>
        /// Writes graph snapshots under <c>&lt;root&gt;/.codecontext/cache/</c> after commits. Off by
        /// default while nothing reads them; set <c>CodeContext:CacheEnabled</c> (environment
        /// variable <c>CodeContext__CacheEnabled=true</c>) to turn it on.
        /// </summary>
        public bool CacheEnabled { get; set; }

        /// <summary>
        /// Seconds without a further commit before a changed graph is written. From configuration,
        /// clamped to 1..86,400. When larger than <see cref="SnapshotMaxDelaySeconds"/>, the maximum
        /// wins: the graph is written that long after it first changed.
        /// </summary>
        public int SnapshotQuietSeconds { get; set; } = 10;

        /// <summary>
        /// Seconds after the graph first changed by which it is written however often it keeps changing.
        /// From configuration, clamped to 1..86,400. It wins over a longer <see cref="SnapshotQuietSeconds"/>.
        /// </summary>
        public int SnapshotMaxDelaySeconds { get; set; } = 120;

        /// <summary>The snapshot key file; null means <c>~/.codecontext/cache.key</c>. Set in code only, never from configuration.</summary>
        public string? CacheKeyPath { get; set; }
    }
}
