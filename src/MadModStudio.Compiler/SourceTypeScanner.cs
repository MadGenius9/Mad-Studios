using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MadModStudio.Compiler;

/// <summary>Lists the full names of types declared in C# source (syntax only, no compilation).</summary>
public static class SourceTypeScanner
{
    public static HashSet<string> DeclaredTypeNames(IEnumerable<string> files)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f); } catch (IOException) { continue; }
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            foreach (var t in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var parts = new Stack<string>();
                Microsoft.CodeAnalysis.SyntaxNode? n = t;
                while (n != null)
                {
                    switch (n)
                    {
                        case BaseTypeDeclarationSyntax bt:
                            parts.Push(bt.Identifier.Text + (bt is TypeDeclarationSyntax { TypeParameterList: { } tp } ? "`" + tp.Parameters.Count : ""));
                            break;
                        case BaseNamespaceDeclarationSyntax ns:
                            parts.Push("NS:" + ns.Name);
                            break;
                    }
                    n = n.Parent;
                }
                var ns2 = string.Join(".", parts.Where(p => p.StartsWith("NS:")).Select(p => p[3..]));
                var types = parts.Where(p => !p.StartsWith("NS:")).ToList();
                var typeName = string.Join("+", types);
                result.Add(ns2.Length == 0 ? typeName : ns2 + "." + typeName);
            }
        }
        return result;
    }
}
