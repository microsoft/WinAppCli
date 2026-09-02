// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Decodes IL metadata signatures into readable type-name strings for the
/// <see cref="WinMdParser"/>. AOT-safe: pure metadata reads, no reflection.
/// </summary>
internal sealed class SimpleTypeProvider : ISignatureTypeProvider<string, object?>, IConstructedTypeProvider<string>, ISZArrayTypeProvider<string>, ISimpleTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        return typeCode switch
        {
            PrimitiveTypeCode.Boolean => "Boolean",
            PrimitiveTypeCode.Byte => "Byte",
            PrimitiveTypeCode.SByte => "SByte",
            PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.UInt16 => "UInt16",
            PrimitiveTypeCode.Int32 => "Int32",
            PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.Int64 => "Int64",
            PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.Double => "Double",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.Object => "Object",
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => typeCode.ToString(),
        };
    }

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        WinMdParser.BuildFullTypeName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        BuildReferenceName(reader, handle, depth: 0);

    /// <summary>
    /// Names a referenced type the way <see cref="WinMdParser.BuildFullTypeName"/> names a
    /// defined one. A reference to a nested type carries only its own simple name and
    /// resolves its parent through <see cref="TypeReference.ResolutionScope"/>, so reading
    /// its namespace alone renders a field or parameter as a bare <c>Inner</c> — a name
    /// that matches no indexed type, or matches an arbitrary same-named one.
    /// </summary>
    private static string BuildReferenceName(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        TypeReference typeReference = reader.GetTypeReference(handle);
        string name = reader.GetString(typeReference.Name);
        if (depth < MaxNestingDepth && typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            string outer = BuildReferenceName(reader, (TypeReferenceHandle)typeReference.ResolutionScope, depth + 1);
            return outer + "." + name;
        }
        string ns = reader.GetString(typeReference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>Bounds the resolution-scope walk against malformed or circular metadata.</summary>
    private const int MaxNestingDepth = 32;

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        string text = genericType;
        int num = text.IndexOf('`');
        if (num >= 0)
        {
            text = text.Substring(0, num);
        }
        return text + "<" + string.Join(", ", typeArguments) + ">";
    }

    public string GetGenericMethodParameter(object? genericContext, int index) => $"TMethod{index}";

    public string GetGenericTypeParameter(object? genericContext, int index) => $"T{index}";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}

/// <summary>
/// Minimal custom-attribute decoder used to pull the message out of
/// <c>[Deprecated]</c>/<c>[Obsolete]</c> attributes.
/// </summary>
internal sealed class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<object?>
{
    public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
    public object? GetSystemType() => null;
    public object? GetSZArrayType(object? elementType) => null;
    public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
    public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
    public object? GetTypeFromSerializedName(string name) => null;
    public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(object? type) => false;
}
