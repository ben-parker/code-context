
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
        /// Loads the newest valid graph snapshot under <c>&lt;root&gt;/.codecontext/cache/</c> at start, serves
        /// it labelled while the startup scan verifies it, and writes snapshots after commits. On by
        /// default; <c>start --no-cache</c> or <c>CodeContext:CacheEnabled=false</c> turn it off.
        /// </summary>
        public bool CacheEnabled { get; set; } = true;

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
