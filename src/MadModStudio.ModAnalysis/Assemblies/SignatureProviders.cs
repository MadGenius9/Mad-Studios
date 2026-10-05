using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace MadModStudio.ModAnalysis.Assemblies;

public sealed record GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters)
{
    public static readonly GenericContext Empty = new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
}

/// <summary>Decodes signatures into readable C#-like type names without loading any types.</summary>
internal sealed class DisplayTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly DisplayTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        MetadataNames.DisplayName(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        MetadataNames.DisplayName(reader, handle);

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        var tick = genericType.IndexOf('`');
        var name = tick >= 0 ? genericType[..tick] : genericType;
        return name + "<" + string.Join(", ", typeArguments) + ">";
    }

    public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
        index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "!!" + index;

    public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
        index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "!" + index;
}

/// <summary>Custom attribute decoding. Enum arguments from external assemblies are assumed to be int32 (true for Harmony's enums).</summary>
internal sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    public static readonly AttributeTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => DisplayTypeProvider.Instance.GetPrimitiveType(typeCode);
    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataNames.FullName(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataNames.FullName(reader, handle);
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string type) => type == "System.Type";
}

internal static class MetadataNames
{
    public static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = reader.GetString(def.Name);
        var declaring = def.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, declaring) + "+" + name;
        var ns = reader.GetString(def.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var r = reader.GetTypeReference(handle);
        var name = reader.GetString(r.Name);
        if (r.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(reader, (TypeReferenceHandle)r.ResolutionScope) + "+" + name;
        var ns = reader.GetString(r.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Short, readable name used inside signatures (no namespace, generic arity stripped).</summary>
    public static string DisplayName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var def = reader.GetTypeDefinition(handle);
        var declaring = def.GetDeclaringType();
        var name = StripArity(reader.GetString(def.Name));
        return declaring.IsNil ? name : DisplayName(reader, declaring) + "." + name;
    }

    public static string DisplayName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var r = reader.GetTypeReference(handle);
        var name = reader.GetString(r.Name);
        var ns = reader.GetString(r.Namespace);
        var simple = (ns, name) switch
        {
            ("System", "Void") => "void",
            ("System", "Object") => "object",
            ("System", "String") => "string",
            ("System", "Boolean") => "bool",
            ("System", "Int32") => "int",
            ("System", "Single") => "float",
            _ => StripArity(name),
        };
        if (r.ResolutionScope.Kind == HandleKind.TypeReference)
            return DisplayName(reader, (TypeReferenceHandle)r.ResolutionScope) + "." + simple;
        return simple;
    }

    public static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    public static string? AssemblyOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        var r = reader.GetTypeReference(handle);
        return r.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)r.ResolutionScope).Name),
            HandleKind.TypeReference => AssemblyOf(reader, (TypeReferenceHandle)r.ResolutionScope),
            _ => null,
        };
    }

    /// <summary>Name of the attribute type of a custom attribute (full name).</summary>
    public static string AttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                return mr.Parent.Kind switch
                {
                    HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)mr.Parent),
                    HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)mr.Parent),
                    _ => "",
                };
            case HandleKind.MethodDefinition:
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                return FullName(reader, md.GetDeclaringType());
            default:
                return "";
        }
    }

    public static string TypeHandleName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(DisplayTypeProvider.Instance, GenericContext.Empty),
        _ => "",
    };
}
