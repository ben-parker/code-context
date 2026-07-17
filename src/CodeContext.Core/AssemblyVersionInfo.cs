using System.Reflection;

namespace CodeContext.Core;

/// <summary>
/// Shared reader for the git-derived version stamped onto assemblies at build time
/// by Nerdbank.GitVersioning. Centralizes the "read the informational version, fall
/// back to the assembly version" pattern so call sites don't duplicate it.
/// </summary>
public static class AssemblyVersionInfo
{
    /// <summary>
    /// Returns the assembly's <see cref="AssemblyInformationalVersionAttribute"/> value
    /// (the nbgv git-derived version), falling back to <paramref name="fallback"/> if given,
    /// otherwise to the plain assembly version, and finally to <c>"0.0.0"</c>.
    /// </summary>
    public static string GetInformationalVersion(Assembly assembly, string? fallback = null)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            return informational;
        }

        return fallback ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
