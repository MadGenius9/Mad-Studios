namespace MadModStudio.ModAnalysis.Harmony;

public sealed record HarmonyPatchInfo
{
    public string PatchClass { get; init; } = "";
    public string? PatchMethod { get; init; }
    /// <summary>Prefix, Postfix, Transpiler, Finalizer, or Unknown.</summary>
    public string PatchKind { get; init; } = "Unknown";
    /// <summary>Target type as written (may be a simple or fully qualified name).</summary>
    public string? TargetType { get; init; }
    public string? TargetMethod { get; init; }
    /// <summary>Normal, Getter, Setter, Constructor, StaticConstructor, Enumerator, Async.</summary>
    public string MethodType { get; init; } = "Normal";
    public IReadOnlyList<string>? ArgumentTypes { get; init; }
    /// <summary>"Metadata" (from a compiled DLL) or "Source" (from C# source).</summary>
    public string Origin { get; init; } = "Metadata";
    public string? File { get; init; }
    public int? Line { get; init; }
    /// <summary>True when the target is computed at runtime (TargetMethod/TargetMethods) and cannot be resolved statically.</summary>
    public bool IsDynamicTarget { get; init; }
    /// <summary>
    /// Parameter names of the patch method that Harmony resolves by name (__instance, ___field, original parameter
    /// names, ...). Parameters remapped with [HarmonyArgument] are left out; null when unknown or remapped at method level.
    /// </summary>
    public IReadOnlyList<string>? PatchParameterNames { get; init; }

    public string TargetDisplay
    {
        get
        {
            if (IsDynamicTarget) return "(computed at runtime)";
            var t = TargetType ?? "?";
            var m = MethodType switch
            {
                "Constructor" => ".ctor",
                "StaticConstructor" => ".cctor",
                "Getter" => "get_" + (TargetMethod ?? "?"),
                "Setter" => "set_" + (TargetMethod ?? "?"),
                _ => TargetMethod ?? "?",
            };
            var args = ArgumentTypes is { Count: > 0 } ? "(" + string.Join(", ", ArgumentTypes) + ")" : "";
            return $"{t}.{m}{args}";
        }
    }
}

/// <summary>Neutral representation of one [HarmonyPatch(...)] attribute's arguments, from metadata or source.</summary>
public sealed class HarmonyAttributeArgs
{
    public List<string> Types { get; } = new();
    public List<string> Strings { get; } = new();
    public string? MethodType { get; set; }
    public List<string>? ArgumentTypes { get; set; }
}

public static class HarmonyPatchMerger
{
    public static readonly string[] PatchMethodNames = { "Prefix", "Postfix", "Transpiler", "Finalizer" };

    private static readonly string[] MethodTypeNames = { "Normal", "Getter", "Setter", "Constructor", "StaticConstructor", "Enumerator", "Async" };

    public static string MethodTypeFromInt(int value) =>
        value >= 0 && value < MethodTypeNames.Length ? MethodTypeNames[value] : value.ToString();

    public static (string? Type, string? Method, string MethodType, List<string>? Args) Combine(IEnumerable<HarmonyAttributeArgs> attributes)
    {
        string? type = null, method = null, methodType = null;
        List<string>? args = null;
        foreach (var a in attributes)
        {
            if (a.Types.Count > 0) type = a.Types[0];
            if (a.Strings.Count >= 2 && a.Types.Count == 0)
            {
                // (string typeName, string methodName)
                type = a.Strings[0];
                method = a.Strings[1];
            }
            else if (a.Strings.Count == 1) method = a.Strings[0];
            if (a.MethodType != null) methodType = a.MethodType;
            if (a.ArgumentTypes != null) args = a.ArgumentTypes;
        }
        return (StripAssemblyQualifier(type), method, methodType ?? "Normal", args);
    }

    public static string? StripAssemblyQualifier(string? typeName)
    {
        if (typeName is null) return null;
        // "Ns.Type, Assembly-CSharp, Version=..." -> "Ns.Type" (ignore commas inside generic brackets)
        var depth = 0;
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (c == '[' || c == '<') depth++;
            else if (c == ']' || c == '>') depth--;
            else if (c == ',' && depth == 0) return typeName[..i].Trim();
        }
        return typeName.Trim();
    }

    public static string KindFromMethod(string methodName, IEnumerable<string> attributeNames)
    {
        foreach (var a in attributeNames)
        {
            var simple = a.Split('.').Last().Replace("Attribute", "");
            switch (simple)
            {
                case "HarmonyPrefix": return "Prefix";
                case "HarmonyPostfix": return "Postfix";
                case "HarmonyTranspiler": return "Transpiler";
                case "HarmonyFinalizer": return "Finalizer";
            }
        }
        return PatchMethodNames.FirstOrDefault(n => n == methodName) ?? "Unknown";
    }

    public static bool IsHarmonyPatchAttribute(string attributeName)
    {
        var simple = attributeName.Split('.', '+').Last();
        return simple is "HarmonyPatch" or "HarmonyPatchAttribute";
    }

    /// <summary>[HarmonyArgument] remaps a patch parameter to an original parameter, so its own name doesn't matter.</summary>
    public static bool IsHarmonyArgumentAttribute(string attributeName)
    {
        var simple = attributeName.Split('.', '+').Last();
        return simple is "HarmonyArgument" or "HarmonyArgumentAttribute";
    }

    public static bool IsPatchKindAttribute(string attributeName)
    {
        var simple = attributeName.Split('.', '+').Last().Replace("Attribute", "");
        return simple is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer";
    }
}
