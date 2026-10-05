using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MadModStudio.Core.IO;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.ModAnalysis.Assemblies;

public sealed class AssemblyInspectionOptions
{
    /// <summary>Include private/internal members (useful for mods; may be skipped for huge game assemblies).</summary>
    public bool IncludeNonPublic { get; init; } = true;
    /// <summary>When non-public members are excluded, still include protected ones (callable from derived types in mods).</summary>
    public bool IncludeProtected { get; init; } = true;
    public bool IncludeMembers { get; init; } = true;
    public bool ComputeHash { get; init; } = true;
    public bool CollectExternalReferences { get; init; } = true;
    public long MaxFileSizeBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>
/// Reads .NET assemblies via System.Reflection.Metadata. The file is parsed as data: it is never loaded into an
/// AppDomain/AssemblyLoadContext and no code from it can run.
/// </summary>
public sealed class AssemblyInspector
{
    public AssemblyReport Inspect(string path, AssemblyInspectionOptions? options = null)
    {
        options ??= new AssemblyInspectionOptions();
        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch (Exception ex) { return new AssemblyReport { FilePath = path, ReadError = ex.Message }; }

        if (!fi.Exists) return new AssemblyReport { FilePath = path, ReadError = "File not found." };
        if (fi.Length > options.MaxFileSizeBytes)
            return new AssemblyReport { FilePath = path, FileSize = fi.Length, ReadError = $"File is larger than {options.MaxFileSizeBytes:N0} bytes; skipped." };

        string? hash = null;
        try { if (options.ComputeHash) hash = FileUtil.Sha256(path); }
        catch (IOException ex) { return new AssemblyReport { FilePath = path, FileSize = fi.Length, ReadError = $"Unreadable: {ex.Message}" }; }

        var report = new AssemblyReport { FilePath = path, FileSize = fi.Length, Sha256 = hash };
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata)
            {
                report.ReadError = "Not a managed .NET assembly (native DLL or no metadata).";
                return report;
            }
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                report.ReadError = "Metadata module without an assembly manifest.";
                return report;
            }
            report.IsManagedAssembly = true;
            ReadManifest(reader, report);
            ReadTypes(reader, report, options);
            if (options.CollectExternalReferences) ReadExternalReferences(reader, report);
        }
        catch (BadImageFormatException ex)
        {
            report.ReadError = $"Invalid or corrupt PE image: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            report.ReadError = $"Unreadable metadata: {ex.Message}";
        }
        catch (IOException ex)
        {
            report.ReadError = $"Unreadable: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            report.ReadError = $"Access denied: {ex.Message}";
        }
        return report;
    }

    /// <summary>Fast check whether a file is a managed assembly (used when scanning game folders).</summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(fs);
            return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
        }
        catch { return false; }
    }

    /// <summary>Returns the assembly name and version without reading types.</summary>
    public static (string Name, Version Version, IReadOnlyList<AssemblyReferenceInfo> References)? ReadIdentity(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var r = pe.GetMetadataReader();
            if (!r.IsAssembly) return null;
            var def = r.GetAssemblyDefinition();
            var refs = r.AssemblyReferences.Select(h => r.GetAssemblyReference(h))
                .Select(a => new AssemblyReferenceInfo(r.GetString(a.Name), a.Version.ToString(), Token(r, a.PublicKeyOrToken, isToken: true))).ToList();
            return (r.GetString(def.Name), def.Version, refs);
        }
        catch { return null; }
    }

    private static void ReadManifest(MetadataReader reader, AssemblyReport report)
    {
        var def = reader.GetAssemblyDefinition();
        report.Name = reader.GetString(def.Name);
        report.Version = def.Version.ToString();
        report.Culture = def.Culture.IsNil ? null : reader.GetString(def.Culture);
        if (!def.PublicKey.IsNil)
        {
            var key = reader.GetBlobBytes(def.PublicKey);
            if (key.Length > 0)
            {
                var hash = System.Security.Cryptography.SHA1.HashData(key);
                report.PublicKeyToken = Convert.ToHexString(hash.Reverse().Take(8).ToArray()).ToLowerInvariant();
            }
        }
        report.Mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString();

        foreach (var h in reader.AssemblyReferences)
        {
            var a = reader.GetAssemblyReference(h);
            report.References.Add(new AssemblyReferenceInfo(reader.GetString(a.Name), a.Version.ToString(),
                Token(reader, a.PublicKeyOrToken, (a.Flags & AssemblyFlags.PublicKey) == 0)));
        }

        foreach (var ah in def.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(ah);
            if (MetadataNames.AttributeTypeName(reader, attr) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
            try
            {
                var v = attr.DecodeValue(AttributeTypeProvider.Instance);
                if (v.FixedArguments.Length > 0) report.TargetFramework = v.FixedArguments[0].Value as string;
            }
            catch (BadImageFormatException) { }
        }
        report.ReferencesHarmony = report.References.Any(r => r.Name is "0Harmony" or "Harmony" or "HarmonyLib");
    }

    private static string? Token(MetadataReader reader, BlobHandle blob, bool isToken)
    {
        if (blob.IsNil) return null;
        var bytes = reader.GetBlobBytes(blob);
        if (bytes.Length == 0) return null;
        if (!isToken) bytes = System.Security.Cryptography.SHA1.HashData(bytes).Reverse().Take(8).ToArray();
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void ReadTypes(MetadataReader reader, AssemblyReport report, AssemblyInspectionOptions options)
    {
        foreach (var th in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(th);
            var name = reader.GetString(td.Name);
            if (name == "<Module>") continue;

            var visibility = td.Attributes & TypeAttributes.VisibilityMask;
            var isNested = !td.GetDeclaringType().IsNil;
            var isPublic = visibility is TypeAttributes.Public or TypeAttributes.NestedPublic;
            var compilerGenerated = name.Contains('<') || name.Contains('>');
            if (compilerGenerated) continue;
            if (!options.IncludeNonPublic && !isPublic) continue;

            var typeParams = td.GetGenericParameters().Select(g => reader.GetString(reader.GetGenericParameter(g).Name)).ToImmutableArray();
            var ctx = new GenericContext(typeParams, ImmutableArray<string>.Empty);

            var baseType = td.BaseType.IsNil ? null : SafeName(reader, td.BaseType);
            var kind = (td.Attributes & TypeAttributes.Interface) != 0 ? "interface"
                : baseType == "System.Enum" ? "enum"
                : baseType == "System.ValueType" ? "struct"
                : baseType == "System.MulticastDelegate" ? "delegate"
                : "class";

            var typeAttrs = td.GetCustomAttributes().Select(a => MetadataNames.AttributeTypeName(reader, reader.GetCustomAttribute(a))).ToList();
            var interfaces = td.GetInterfaceImplementations()
                .Select(i => SafeName(reader, reader.GetInterfaceImplementation(i).Interface)).ToList();

            var fullName = MetadataNames.FullName(reader, th);
            var ns = isNested ? NamespaceOfNested(reader, th) : reader.GetString(td.Namespace);
            var type = new TypeReport
            {
                Namespace = ns,
                Name = MetadataNames.StripArity(name),
                FullName = fullName,
                Kind = kind,
                BaseType = baseType,
                Interfaces = interfaces,
                IsPublic = isPublic,
                IsNested = isNested,
                IsAbstract = (td.Attributes & TypeAttributes.Abstract) != 0,
                IsSealed = (td.Attributes & TypeAttributes.Sealed) != 0,
                Attributes = typeAttrs,
            };

            if (interfaces.Any(i => i == "IModApi" || i.EndsWith(".IModApi")))
                report.ModApiEntryPoints.Add(fullName);

            var methodAttrs = new Dictionary<MethodDefinitionHandle, List<string>>();
            if (options.IncludeMembers)
                ReadMembers(reader, td, type, ctx, options, methodAttrs);

            report.Types.Add(type);
            DetectHarmonyPatches(reader, th, td, type, report);
        }
    }

    private static string NamespaceOfNested(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        while (!td.GetDeclaringType().IsNil) td = reader.GetTypeDefinition(td.GetDeclaringType());
        return reader.GetString(td.Namespace);
    }

    private static string SafeName(MetadataReader reader, EntityHandle handle)
    {
        try { return MetadataNames.TypeHandleName(reader, handle); }
        catch (BadImageFormatException) { return "?"; }
    }

    private static void ReadMembers(MetadataReader reader, TypeDefinition td, TypeReport type, GenericContext typeCtx,
        AssemblyInspectionOptions options, Dictionary<MethodDefinitionHandle, List<string>> methodAttrs)
    {
        var accessorMethods = new HashSet<MethodDefinitionHandle>();
        foreach (var ph in td.GetProperties())
        {
            var p = reader.GetPropertyDefinition(ph);
            var acc = p.GetAccessors();
            if (!acc.Getter.IsNil) accessorMethods.Add(acc.Getter);
            if (!acc.Setter.IsNil) accessorMethods.Add(acc.Setter);
            var accessor = !acc.Getter.IsNil ? acc.Getter : acc.Setter;
            var isPublic = false; var isStatic = false;
            if (!accessor.IsNil)
            {
                var am = reader.GetMethodDefinition(accessor);
                isPublic = (am.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                isStatic = (am.Attributes & MethodAttributes.Static) != 0;
            }
            if (!options.IncludeNonPublic && !isPublic && !(options.IncludeProtected && !accessor.IsNil && IsProtected(reader.GetMethodDefinition(accessor).Attributes))) continue;
            string propType;
            try { propType = p.DecodeSignature(DisplayTypeProvider.Instance, typeCtx).ReturnType; }
            catch (BadImageFormatException) { propType = "?"; }
            var name = reader.GetString(p.Name);
            type.Members.Add(new MemberReport
            {
                Kind = "Property",
                Name = name,
                ReturnType = propType,
                Signature = $"{propType} {name} {{ {(acc.Getter.IsNil ? "" : "get; ")}{(acc.Setter.IsNil ? "" : "set; ")}}}",
                IsPublic = isPublic,
                IsStatic = isStatic,
            });
        }
        foreach (var eh in td.GetEvents())
        {
            var e = reader.GetEventDefinition(eh);
            var acc = e.GetAccessors();
            if (!acc.Adder.IsNil) accessorMethods.Add(acc.Adder);
            if (!acc.Remover.IsNil) accessorMethods.Add(acc.Remover);
            var name = reader.GetString(e.Name);
            type.Members.Add(new MemberReport { Kind = "Event", Name = name, Signature = $"event {SafeName(reader, e.Type)} {name}", IsPublic = true });
        }
        foreach (var fh in td.GetFields())
        {
            var f = reader.GetFieldDefinition(fh);
            var name = reader.GetString(f.Name);
            if (name.Contains('<')) continue; // backing fields
            var isPublic = (f.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
            var fieldAccess = f.Attributes & FieldAttributes.FieldAccessMask;
            var fieldProtected = fieldAccess is FieldAttributes.Family or FieldAttributes.FamORAssem;
            if (!options.IncludeNonPublic && !isPublic && !(options.IncludeProtected && fieldProtected)) continue;
            string ft;
            try { ft = f.DecodeSignature(DisplayTypeProvider.Instance, typeCtx); }
            catch (BadImageFormatException) { ft = "?"; }
            type.Members.Add(new MemberReport
            {
                Kind = "Field",
                Name = name,
                ReturnType = ft,
                Signature = $"{((f.Attributes & FieldAttributes.Static) != 0 ? "static " : "")}{ft} {name}",
                IsPublic = isPublic,
                IsStatic = (f.Attributes & FieldAttributes.Static) != 0,
            });
        }
        foreach (var mh in td.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            var name = reader.GetString(m.Name);
            if (name.Contains('<')) continue;
            var isPublic = (m.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            var attrs = m.GetCustomAttributes().Select(a => MetadataNames.AttributeTypeName(reader, reader.GetCustomAttribute(a))).ToList();
            methodAttrs[mh] = attrs;
            if (accessorMethods.Contains(mh)) continue;
            if (!options.IncludeNonPublic && !isPublic && !(options.IncludeProtected && IsProtected(m.Attributes))) continue;

            var methodParams = m.GetGenericParameters().Select(g => reader.GetString(reader.GetGenericParameter(g).Name)).ToImmutableArray();
            var ctx = typeCtx with { MethodParameters = methodParams };
            string ret; IReadOnlyList<string> ptypes;
            try
            {
                var sig = m.DecodeSignature(DisplayTypeProvider.Instance, ctx);
                ret = sig.ReturnType;
                ptypes = sig.ParameterTypes;
            }
            catch (BadImageFormatException) { ret = "?"; ptypes = Array.Empty<string>(); }

            var pnames = new string[ptypes.Count];
            foreach (var parh in m.GetParameters())
            {
                var par = reader.GetParameter(parh);
                if (par.SequenceNumber > 0 && par.SequenceNumber <= pnames.Length)
                    pnames[par.SequenceNumber - 1] = reader.GetString(par.Name);
            }
            var plist = string.Join(", ", ptypes.Select((t, i) => string.IsNullOrEmpty(pnames[i]) ? t : $"{t} {pnames[i]}"));
            var isCtor = name is ".ctor" or ".cctor";
            var isStatic = (m.Attributes & MethodAttributes.Static) != 0;
            var generic = methodParams.Length > 0 ? "<" + string.Join(", ", methodParams) + ">" : "";
            type.Members.Add(new MemberReport
            {
                Kind = isCtor ? "Constructor" : "Method",
                Name = name,
                ReturnType = isCtor ? null : ret,
                ParameterCount = ptypes.Count,
                Signature = isCtor
                    ? $"{(isStatic ? "static " : "")}{type.Name}({plist})"
                    : $"{(isStatic ? "static " : "")}{ret} {name}{generic}({plist})",
                IsPublic = isPublic,
                IsStatic = isStatic,
                IsVirtual = (m.Attributes & MethodAttributes.Virtual) != 0,
                Attributes = attrs,
            });
        }
    }

    private static bool IsProtected(MethodAttributes a)
    {
        var access = a & MethodAttributes.MemberAccessMask;
        return access is MethodAttributes.Family or MethodAttributes.FamORAssem;
    }

    private static void DetectHarmonyPatches(MetadataReader reader, TypeDefinitionHandle th, TypeDefinition td, TypeReport type, AssemblyReport report)
    {
        var classArgs = new List<HarmonyAttributeArgs>();
        foreach (var ah in td.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(ah);
            if (!HarmonyPatchMerger.IsHarmonyPatchAttribute(MetadataNames.AttributeTypeName(reader, attr))) continue;
            classArgs.Add(DecodeHarmonyArgs(attr, reader));
        }

        var patchMethods = new List<(string Name, string Kind, List<HarmonyAttributeArgs> Args, bool Dynamic)>();
        var hasTargetMethod = false;
        foreach (var mh in td.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            var name = reader.GetString(m.Name);
            var attrNames = new List<string>();
            var methodArgs = new List<HarmonyAttributeArgs>();
            foreach (var ah in m.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(ah);
                var an = MetadataNames.AttributeTypeName(reader, attr);
                attrNames.Add(an);
                if (HarmonyPatchMerger.IsHarmonyPatchAttribute(an)) methodArgs.Add(DecodeHarmonyArgs(attr, reader));
                if (an.EndsWith("HarmonyTargetMethod") || an.EndsWith("HarmonyTargetMethods")) hasTargetMethod = true;
            }
            if (name is "TargetMethod" or "TargetMethods") hasTargetMethod = true;
            var kind = HarmonyPatchMerger.KindFromMethod(name, attrNames);
            if (kind != "Unknown" || methodArgs.Count > 0)
                patchMethods.Add((name, kind, methodArgs, false));
        }

        if (classArgs.Count == 0 && !patchMethods.Any(p => p.Args.Count > 0)) return;
        // Methods named Prefix/Postfix in a class with no Harmony attributes are not patches.
        foreach (var pm in patchMethods)
        {
            var (t, mth, mt, args) = HarmonyPatchMerger.Combine(classArgs.Concat(pm.Args));
            report.HarmonyPatches.Add(new HarmonyPatchInfo
            {
                PatchClass = type.FullName,
                PatchMethod = pm.Name,
                PatchKind = pm.Kind,
                TargetType = t,
                TargetMethod = mth,
                MethodType = mt,
                ArgumentTypes = args,
                Origin = "Metadata",
                IsDynamicTarget = hasTargetMethod && t is null,
            });
        }
        if (patchMethods.Count == 0)
        {
            var (t, mth, mt, args) = HarmonyPatchMerger.Combine(classArgs);
            report.HarmonyPatches.Add(new HarmonyPatchInfo
            {
                PatchClass = type.FullName,
                TargetType = t,
                TargetMethod = mth,
                MethodType = mt,
                ArgumentTypes = args,
                Origin = "Metadata",
                IsDynamicTarget = hasTargetMethod && t is null,
            });
        }
    }

    private static HarmonyAttributeArgs DecodeHarmonyArgs(CustomAttribute attr, MetadataReader reader)
    {
        var result = new HarmonyAttributeArgs();
        CustomAttributeValue<string> value;
        try { value = attr.DecodeValue(AttributeTypeProvider.Instance); }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException) { return result; }

        foreach (var arg in value.FixedArguments)
        {
            switch (arg.Value)
            {
                case string s when arg.Type == "System.Type":
                    result.Types.Add(s);
                    break;
                case string s:
                    result.Strings.Add(s);
                    break;
                case int i:
                    result.MethodType = HarmonyPatchMerger.MethodTypeFromInt(i);
                    break;
                case ImmutableArray<CustomAttributeTypedArgument<string>> arr:
                    result.ArgumentTypes = arr.Select(a => HarmonyPatchMerger.StripAssemblyQualifier(a.Value?.ToString()) ?? "?").ToList();
                    break;
            }
        }
        return result;
    }

    private static void ReadExternalReferences(MetadataReader reader, AssemblyReport report)
    {
        var seenTypes = new HashSet<string>();
        foreach (var h in reader.TypeReferences)
        {
            var asm = MetadataNames.AssemblyOf(reader, h);
            if (asm is null) continue;
            var name = MetadataNames.FullName(reader, h);
            if (seenTypes.Add(asm + "|" + name)) report.ExternalTypes.Add(new ExternalTypeReference(asm, name));
        }

        var seen = new HashSet<string>();
        foreach (var h in reader.MemberReferences)
        {
            MemberReference mr;
            try { mr = reader.GetMemberReference(h); } catch (BadImageFormatException) { continue; }
            TypeReferenceHandle parent;
            if (mr.Parent.Kind == HandleKind.TypeReference) parent = (TypeReferenceHandle)mr.Parent;
            else if (mr.Parent.Kind == HandleKind.TypeSpecification)
            {
                // Generic instantiation: find the underlying generic type reference if possible.
                var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) continue;
                blob.ReadCompressedInteger();
                var handle = blob.ReadTypeHandle();
                if (handle.Kind != HandleKind.TypeReference) continue;
                parent = (TypeReferenceHandle)handle;
            }
            else continue;

            var asm = MetadataNames.AssemblyOf(reader, parent);
            if (asm is null) continue;
            var typeName = MetadataNames.FullName(reader, parent);
            var name = reader.GetString(mr.Name);
            string kind; int pc = 0; string sig = name;
            try
            {
                if (mr.GetKind() == MemberReferenceKind.Method)
                {
                    var ms = mr.DecodeMethodSignature(DisplayTypeProvider.Instance, GenericContext.Empty);
                    kind = name is ".ctor" or ".cctor" ? "Constructor" : "Method";
                    pc = ms.ParameterTypes.Length;
                    sig = $"{ms.ReturnType} {name}({string.Join(", ", ms.ParameterTypes)})";
                }
                else
                {
                    kind = "Field";
                    sig = $"{mr.DecodeFieldSignature(DisplayTypeProvider.Instance, GenericContext.Empty)} {name}";
                }
            }
            catch (BadImageFormatException) { kind = "Unknown"; }

            if (seen.Add($"{asm}|{typeName}|{sig}"))
                report.ExternalMembers.Add(new ExternalMemberReference(asm, typeName, name, kind, pc, sig));

            if (typeName is "HarmonyLib.Harmony" or "Harmony.HarmonyInstance" && name is "Patch" or "PatchAll" or "CreateClassProcessor")
            {
                if (name == "Patch") report.UsesManualHarmonyPatching = true;
            }
        }
    }
}
