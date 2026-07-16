
namespace CodeContext.Core
{
    public interface ILanguageParser
    {
        string[] SupportedExtensions { get; }
        CodeGraph ParseFile(string filePath, string content);
        CodeGraph ParseFiles(Dictionary<string, string> fileContents);
    }

    /// <summary>
    /// Optional self-description for a parser: a stable display name plus whether the
    /// external tooling it depends on (e.g. Node.js) is actually usable. Status reporting
    /// discovers parser health through this instead of hard-coding parser names.
    /// </summary>
    public interface IParserDiagnostics
    {
        string DisplayName { get; }
        bool IsAvailable { get; }
        /// <summary>Actionable remediation message when <see cref="IsAvailable"/> is false.</summary>
        string? UnavailableReason { get; }
    }
}
