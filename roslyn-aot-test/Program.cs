using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Testing Microsoft.CodeAnalysis with AOT compilation...");
        
        try
        {
            // Create a simple test C# file content
            var testCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
            Console.WriteLine(""Hello World"");
        }
        
        public string TestProperty { get; set; }
    }
    
    public interface ITestInterface
    {
        void InterfaceMethod();
    }
}";

            Console.WriteLine("Creating syntax tree...");
            var syntaxTree = CSharpSyntaxTree.ParseText(testCode);
            
            Console.WriteLine("Creating compilation...");
            var compilation = CSharpCompilation.Create("TestAssembly")
                .AddSyntaxTrees(syntaxTree);
            
            Console.WriteLine("Getting semantic model...");
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            
            Console.WriteLine("Analyzing syntax tree...");
            var root = syntaxTree.GetRoot();
            var classes = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();
            var interfaces = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax>();
            var methods = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>();
            
            Console.WriteLine($"Found {classes.Count()} classes, {interfaces.Count()} interfaces, {methods.Count()} methods");
            
            foreach (var classDecl in classes)
            {
                var symbol = semanticModel.GetDeclaredSymbol(classDecl);
                Console.WriteLine($"- Class: {symbol?.Name} in {symbol?.ContainingNamespace}");
            }
            
            foreach (var interfaceDecl in interfaces)
            {
                var symbol = semanticModel.GetDeclaredSymbol(interfaceDecl);
                Console.WriteLine($"- Interface: {symbol?.Name} in {symbol?.ContainingNamespace}");
            }
            
            foreach (var methodDecl in methods)
            {
                var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                Console.WriteLine($"- Method: {symbol?.Name} in {symbol?.ContainingType?.Name}");
            }
            
            Console.WriteLine("✅ Microsoft.CodeAnalysis works correctly with AOT!");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Microsoft.CodeAnalysis failed with AOT: {ex.Message}");
            Console.WriteLine($"Exception type: {ex.GetType().Name}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Environment.Exit(1);
        }
    }
}