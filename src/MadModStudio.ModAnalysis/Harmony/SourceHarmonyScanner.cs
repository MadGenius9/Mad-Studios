using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MadModStudio.ModAnalysis.Harmony;

/// <summary>Finds Harmony patches in C# source using Roslyn syntax trees (no compilation required).</summary>
public sealed class SourceHarmonyScanner
{
    public IReadOnlyList<HarmonyPatchInfo> ScanFiles(IEnumerable<string> files, string? displayRoot = null)
    {
        var result = new List<HarmonyPatchInfo>();
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            var display = displayRoot is null ? file : Path.GetRelativePath(displayRoot, file).Replace('\\', '/');
            result.AddRange(ScanText(text, display));
        }
        return result;
    }

    public IReadOnlyList<HarmonyPatchInfo> ScanText(string source, string fileName)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var root = tree.GetRoot();
        var result = new List<HarmonyPatchInfo>();

        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var classArgs = HarmonyAttributes(cls.AttributeLists).Select(ParseArgs).ToList();
            var hasTargetMethod = cls.Members.OfType<MethodDeclarationSyntax>().Any(m =>
                m.Identifier.Text is "TargetMethod" or "TargetMethods" ||
                m.AttributeLists.SelectMany(a => a.Attributes).Any(a => Simple(a.Name) is "HarmonyTargetMethod" or "HarmonyTargetMethods"));

            var patchMethods = new List<(MethodDeclarationSyntax Method, string Kind, List<HarmonyAttributeArgs> Args)>();
            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                var attrNames = m.AttributeLists.SelectMany(a => a.Attributes).Select(a => Simple(a.Name)).ToList();
                var methodArgs = HarmonyAttributes(m.AttributeLists).Select(ParseArgs).ToList();
                var kind = HarmonyPatchMerger.KindFromMethod(m.Identifier.Text, attrNames);
                if (kind != "Unknown" || methodArgs.Count > 0) patchMethods.Add((m, kind, methodArgs));
            }
            if (classArgs.Count == 0 && !patchMethods.Any(p => p.Args.Count > 0)) continue;

            var className = FullClassName(cls);
            foreach (var pm in patchMethods)
            {
                var (t, mth, mt, args) = HarmonyPatchMerger.Combine(classArgs.Concat(pm.Args));
                result.Add(new HarmonyPatchInfo
                {
                    PatchClass = className,
                    PatchMethod = pm.Method.Identifier.Text,
                    PatchKind = pm.Kind,
                    TargetType = t,
                    TargetMethod = mth,
                    MethodType = mt,
                    ArgumentTypes = args,
                    Origin = "Source",
                    File = fileName,
                    Line = pm.Method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsDynamicTarget = hasTargetMethod && t is null,
                    PatchParameterNames = PatchParameterNames(pm.Method),
                });
            }
            if (patchMethods.Count == 0)
            {
                var (t, mth, mt, args) = HarmonyPatchMerger.Combine(classArgs);
                result.Add(new HarmonyPatchInfo
                {
                    PatchClass = className,
                    TargetType = t,
                    TargetMethod = mth,
                    MethodType = mt,
                    ArgumentTypes = args,
                    Origin = "Source",
                    File = fileName,
                    Line = cls.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsDynamicTarget = hasTargetMethod && t is null,
                });
            }
        }
        return result;
    }

    /// <summary>Names of the method's parameters that Harmony resolves by name (those without [HarmonyArgument]).</summary>
    private static List<string>? PatchParameterNames(MethodDeclarationSyntax m)
    {
        if (m.AttributeLists.SelectMany(a => a.Attributes).Any(a => HarmonyPatchMerger.IsHarmonyArgumentAttribute(Simple(a.Name)))) return null;
        return m.ParameterList.Parameters
            .Where(p => !p.AttributeLists.SelectMany(a => a.Attributes).Any(a => HarmonyPatchMerger.IsHarmonyArgumentAttribute(Simple(a.Name))))
            .Select(p => p.Identifier.Text).ToList();
    }

    private static IEnumerable<AttributeSyntax> HarmonyAttributes(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Where(a => HarmonyPatchMerger.IsHarmonyPatchAttribute(Simple(a.Name)));

    private static string Simple(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => name.ToString(),
    };

    private static HarmonyAttributeArgs ParseArgs(AttributeSyntax attr)
    {
        var args = new HarmonyAttributeArgs();
        if (attr.ArgumentList is null) return args;
        var extraTypes = new List<string>();
        foreach (var a in attr.ArgumentList.Arguments)
        {
            switch (a.Expression)
            {
                case TypeOfExpressionSyntax t:
                    if (args.Types.Count == 0 && args.Strings.Count == 0 && args.MethodType is null) args.Types.Add(t.Type.ToString());
                    else extraTypes.Add(t.Type.ToString()); // params Type[] argumentTypes
                    break;
                case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression):
                    args.Strings.Add(l.Token.ValueText);
                    break;
                case InvocationExpressionSyntax inv when inv.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }:
                    var expr = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var last = expr switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        IdentifierNameSyntax id => id.Identifier.Text,
                        _ => expr?.ToString(),
                    };
                    if (last != null) args.Strings.Add(last);
                    break;
                case MemberAccessExpressionSyntax ma when ma.Expression.ToString().EndsWith("MethodType"):
                    args.MethodType = ma.Name.Identifier.Text;
                    break;
                case ArrayCreationExpressionSyntax arr:
                    args.ArgumentTypes = TypesInInitializer(arr.Initializer);
                    break;
                case ImplicitArrayCreationExpressionSyntax iarr:
                    args.ArgumentTypes = TypesInInitializer(iarr.Initializer);
                    break;
                case CollectionExpressionSyntax coll:
                    args.ArgumentTypes = coll.Elements.OfType<ExpressionElementSyntax>()
                        .Select(e => e.Expression is TypeOfExpressionSyntax t ? t.Type.ToString() : e.Expression.ToString()).ToList();
                    break;
            }
        }
        if (extraTypes.Count > 0) args.ArgumentTypes = extraTypes;
        return args;
    }

    private static List<string> TypesInInitializer(InitializerExpressionSyntax? init) =>
        init?.Expressions.Select(e => e is TypeOfExpressionSyntax t ? t.Type.ToString() : e.ToString()).ToList() ?? new List<string>();

    private static string FullClassName(ClassDeclarationSyntax cls)
    {
        var parts = new Stack<string>();
        SyntaxNode? node = cls;
        while (node != null)
        {
            switch (node)
            {
                case ClassDeclarationSyntax c: parts.Push(c.Identifier.Text); break;
                case StructDeclarationSyntax s: parts.Push(s.Identifier.Text); break;
                case BaseNamespaceDeclarationSyntax n: parts.Push(n.Name.ToString()); break;
            }
            node = node.Parent;
        }
        return string.Join(".", parts);
    }
}
