using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeContext.CSharp.Worker.State;

/// <summary>
/// Which build of the worker, and of what it binds against, produced a set of facts. A rebuilt
/// development worker with uncommitted edits reports the same informational version as the build
/// before it, so the version alone cannot keep a changed worker from trusting an old state: each
/// assembly whose code decides a fact contributes its module version id, which every build changes.
/// The runtime's core library stands for the framework assemblies the fallback binds against.
/// </summary>
internal static class WorkerIdentity
{
    public static string Current { get; } = Compute();

    private static string Compute()
    {
        var worker = typeof(WorkerIdentity).Assembly;
        var parts = new List<string> { InformationalVersion(worker) };
        foreach (var assembly in new[]
                 {
                     worker,
                     typeof(Parser.Protocol.ParserProtocol).Assembly,
                     typeof(Compilation).Assembly,
                     typeof(CSharpCompilation).Assembly,
                     typeof(object).Assembly,
                 })
        {
            parts.Add(assembly.ManifestModule.ModuleVersionId.ToString("N"));
        }
        return string.Join('|', parts);
    }

    private static string InformationalVersion(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
}
