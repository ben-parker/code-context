using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// Decides whether an edit can change what any other file binds to. Binding is nearly all the
/// cost of analysis, and most edits are inside a method, so this is what makes an edit cheap.
///
/// The rule is Roslyn's own: <c>AreEquivalent(topLevel: true)</c> ignores method and accessor
/// bodies, expression bodies and the initializers of non-const fields, and nothing else. Each
/// of those is invisible from another file, because C# has no inference across members: a
/// member's type is written in its declaration, so no body can flow into a signature. Const
/// initializers, attribute arguments, default parameter values and enum member values are all
/// compared, as they must be: a constant's value can choose an overload elsewhere.
/// <c>CSharpWorkerEditClassificationTests</c> pins each of these.
/// </summary>
public static class EditClassifier
{
    /// <summary>
    /// True unless the edit touched only what no other file can see. Top-level statements
    /// are also private to their file, so they are compared apart from the declarations
    /// beside them; whether a file has any at all is visible, as it decides the entry point.
    /// </summary>
    public static bool CanAffectOtherFiles(SyntaxTree before, SyntaxTree after)
    {
        var oldRoot = (CompilationUnitSyntax)before.GetRoot();
        var newRoot = (CompilationUnitSyntax)after.GetRoot();
        if (SyntaxFactory.AreEquivalent(oldRoot, newRoot, topLevel: true)) return false;

        var oldHasStatements = oldRoot.Members.Any(IsStatement);
        if (oldHasStatements != newRoot.Members.Any(IsStatement) || !oldHasStatements) return true;

        return !SyntaxFactory.AreEquivalent(WithoutStatements(oldRoot), WithoutStatements(newRoot), topLevel: true);
    }

    private static bool IsStatement(MemberDeclarationSyntax member) => member is GlobalStatementSyntax;

    private static CompilationUnitSyntax WithoutStatements(CompilationUnitSyntax root)
        => root.WithMembers(SyntaxFactory.List(root.Members.Where(member => !IsStatement(member))));
}
