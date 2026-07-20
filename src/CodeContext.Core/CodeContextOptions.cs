
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
    }
}
