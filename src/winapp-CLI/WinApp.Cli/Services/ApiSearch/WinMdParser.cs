// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Reads <c>.winmd</c>/<c>.dll</c> PE metadata and extracts public types,
/// their members, enum values, and <c>[Deprecated]</c>/<c>[Obsolete]</c>
/// messages. AOT-safe: uses <see cref="System.Reflection.Metadata"/> only.
/// </summary>
internal static class WinMdParser
{
    /// <summary>
    /// Outcome of parsing one metadata file: the types that were read plus, when
    /// the file could not be read in full, a diagnostic describing why. The
    /// diagnostic exists so the cache builder can mark the resulting package
    /// cache as incomplete — an unreadable file that silently produced zero types
    /// would otherwise make <c>check-property</c> and <c>members</c> report
    /// authoritative "does not exist" answers about an API that was never indexed.
    /// </summary>
    internal sealed record WinMdParseResult(List<WinMdTypeInfo> Types, string? Error);

    public static WinMdParseResult ParseFile(string filePath)
    {
        var results = new List<WinMdTypeInfo>();
        try
        {
            using FileStream peStream = File.OpenRead(filePath);
            using var peReader = new PEReader(peStream);
            if (!peReader.HasMetadata)
            {
                return new WinMdParseResult(results, "file contains no CLI metadata");
            }
            MetadataReader reader = peReader.GetMetadataReader();
            var typeProvider = new SimpleTypeProvider();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition typeDef = reader.GetTypeDefinition(handle);
                string name = reader.GetString(typeDef.Name);
                if (ShouldSkipType(name, typeDef))
                {
                    continue;
                }
                string ns = BuildNamespace(reader, typeDef);

                TypeKind typeKind = DetermineTypeKind(reader, typeDef);
                string? baseTypeName = GetBaseTypeName(reader, typeDef);
                List<WinMdMemberInfo> members = ParseMembers(reader, typeDef, typeProvider);
                List<string>? enumValues = typeKind == TypeKind.Enum ? ParseEnumValues(reader, typeDef) : null;
                string fullName = BuildFullTypeName(reader, typeDef);
                string? deprecatedMessage = GetDeprecatedMessage(reader, typeDef.GetCustomAttributes());
                results.Add(new WinMdTypeInfo
                {
                    Namespace = ns,
                    Name = name,
                    FullName = fullName,
                    Kind = typeKind,
                    BaseType = baseTypeName,
                    Members = members,
                    EnumValues = enumValues,
                    SourceFile = Path.GetFileName(filePath),
                    DeprecatedMessage = deprecatedMessage
                });
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException
            or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            // A single unparseable metadata file must not fail the whole scan, and
            // writing to stderr would corrupt --json output — so report it to the
            // caller instead, which records it in the package's meta.json.
            return new WinMdParseResult(results, ex.Message);
        }
        return new WinMdParseResult(results, null);
    }

    /// <summary>
    /// Maximum nesting depth walked when qualifying a nested type. Real metadata nests
    /// a handful of levels at most; the bound only stops a malformed or hostile file
    /// whose declaring-type chain is circular from spinning here.
    /// </summary>
    private const int MaxNestingDepth = 32;

    /// <summary>
    /// The fully-qualified name of a type as a caller would write it.
    /// <para>
    /// A nested type carries no namespace of its own — ECMA-335 puts the namespace on
    /// the outermost enclosing type and links the nested type to its parent through the
    /// NestedClass table. Reading <c>typeDef.Namespace</c> for one therefore yields an
    /// empty string, so naming it from its own namespace and name alone drops it into
    /// the global namespace under its bare name. In Win32 metadata that is not a corner
    /// case: <c>_Anonymous_e__Union</c> alone occurs thousands of times, so every one of
    /// those types collides with all the others under a single meaningless name.
    /// </para>
    /// </summary>
    internal static string BuildFullTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        string name = reader.GetString(typeDef.Name);
        if (!typeDef.IsNested)
        {
            string ownNs = reader.GetString(typeDef.Namespace);
            return string.IsNullOrEmpty(ownNs) ? name : ownNs + "." + name;
        }
        var parts = new List<string> { name };
        TypeDefinition outermost = typeDef;
        for (int depth = 0; depth < MaxNestingDepth && outermost.IsNested; depth++)
        {
            TypeDefinitionHandle declaring = outermost.GetDeclaringType();
            if (declaring.IsNil)
            {
                break;
            }
            outermost = reader.GetTypeDefinition(declaring);
            parts.Add(reader.GetString(outermost.Name));
        }
        parts.Reverse();
        // Joined with '.', not the '+' of reflection names, because find-api answers
        // "what do I type in my code" and C# spells a nested type Outer.Inner.
        string nested = string.Join('.', parts);
        string ns = reader.GetString(outermost.Namespace);
        return string.IsNullOrEmpty(ns) ? nested : ns + "." + nested;
    }

    /// <summary>
    /// The namespace a type belongs to, taken from its outermost enclosing type when it
    /// is nested. See <see cref="BuildFullTypeName"/>.
    /// </summary>
    internal static string BuildNamespace(MetadataReader reader, TypeDefinition typeDef)
    {
        TypeDefinition outermost = typeDef;
        for (int depth = 0; depth < MaxNestingDepth && outermost.IsNested; depth++)
        {
            TypeDefinitionHandle declaring = outermost.GetDeclaringType();
            if (declaring.IsNil)
            {
                break;
            }
            outermost = reader.GetTypeDefinition(declaring);
        }
        return reader.GetString(outermost.Namespace);
    }

    internal static bool ShouldSkipType(string name, TypeDefinition typeDef)
    {
        if (string.IsNullOrEmpty(name) || name == "<Module>" || name.StartsWith('<'))
        {
            return true;
        }
        TypeAttributes visibility = typeDef.Attributes & TypeAttributes.VisibilityMask;
        if (visibility != TypeAttributes.Public)
        {
            return visibility != TypeAttributes.NestedPublic;
        }
        return false;
    }

    internal static TypeKind DetermineTypeKind(MetadataReader reader, TypeDefinition typeDef)
    {
        if ((typeDef.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.NotPublic)
        {
            return TypeKind.Interface;
        }
        return GetBaseTypeName(reader, typeDef) switch
        {
            "System.Enum" => TypeKind.Enum,
            "System.ValueType" => TypeKind.Struct,
            "System.MulticastDelegate" or "System.Delegate" => TypeKind.Delegate,
            _ => TypeKind.Class,
        };
    }

    private static string? GetBaseTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        if (typeDef.BaseType.IsNil)
        {
            return null;
        }
        return typeDef.BaseType.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeDefName(reader, (TypeDefinitionHandle)typeDef.BaseType),
            HandleKind.TypeReference => GetTypeRefName(reader, (TypeReferenceHandle)typeDef.BaseType),
            _ => null,
        };
    }

    private static string GetTypeDefName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition typeDef = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(typeDef.Namespace);
        string name = reader.GetString(typeDef.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetTypeRefName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference typeRef = reader.GetTypeReference(handle);
        string ns = reader.GetString(typeRef.Namespace);
        string name = reader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static List<WinMdMemberInfo> ParseMembers(MetadataReader reader, TypeDefinition typeDef, SimpleTypeProvider typeProvider)
    {
        var members = new List<WinMdMemberInfo>();
        var accessorMethods = new HashSet<MethodDefinitionHandle>();

        foreach (PropertyDefinitionHandle property in typeDef.GetProperties())
        {
            PropertyAccessors accessors = reader.GetPropertyDefinition(property).GetAccessors();
            if (!accessors.Getter.IsNil)
            {
                accessorMethods.Add(accessors.Getter);
            }
            if (!accessors.Setter.IsNil)
            {
                accessorMethods.Add(accessors.Setter);
            }
        }
        foreach (EventDefinitionHandle @event in typeDef.GetEvents())
        {
            EventAccessors accessors = reader.GetEventDefinition(@event).GetAccessors();
            if (!accessors.Adder.IsNil)
            {
                accessorMethods.Add(accessors.Adder);
            }
            if (!accessors.Remover.IsNil)
            {
                accessorMethods.Add(accessors.Remover);
            }
            if (!accessors.Raiser.IsNil)
            {
                accessorMethods.Add(accessors.Raiser);
            }
        }

        foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods())
        {
            if (accessorMethods.Contains(methodHandle))
            {
                continue;
            }
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            if (name.StartsWith('.') || name.StartsWith('<') || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            {
                continue;
            }
            // Read the attribute from this method's own handle. Matching by name
            // instead would mark every overload deprecated as soon as one of them is.
            string? deprecated = GetDeprecatedMessage(reader, method.GetCustomAttributes());
            try
            {
                MethodSignature<string> sig = method.DecodeSignature(typeProvider, null);
                List<WinMdParameterInfo> parameters = GetMethodParameters(reader, method, sig);
                string paramText = string.Join(", ", parameters.Select(p => p.Type + " " + p.Name));
                // A static method is called on the type, not on an instance, so a
                // signature that omits it tells a caller to write the wrong thing.
                string staticPrefix = (method.Attributes & MethodAttributes.Static) != 0 ? "static " : string.Empty;
                MethodSignature<string> docSig = method.DecodeSignature(DocIdTypeProvider.Instance, null);
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Method,
                    Signature = $"{staticPrefix}{sig.ReturnType} {name}({paramText})",
                    ReturnType = sig.ReturnType,
                    Parameters = parameters,
                    IsStatic = (method.Attributes & MethodAttributes.Static) != 0,
                    DocParameterTypes = [.. docSig.ParameterTypes],
                    GenericParameterCount = docSig.GenericParameterCount,
                    DeprecatedMessage = deprecated
                });
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
                or InvalidOperationException or ArgumentException)
            {
                // Signature blob we cannot decode — still list the method by name.
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Method,
                    Signature = name + "(/* signature not decodable */)",
                    DeprecatedMessage = deprecated
                });
            }
        }

        foreach (PropertyDefinitionHandle propertyHandle in typeDef.GetProperties())
        {
            PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
            string name = reader.GetString(property.Name);
            string? deprecated = GetDeprecatedMessage(reader, property.GetCustomAttributes());
            try
            {
                string returnType = property.DecodeSignature(typeProvider, null).ReturnType;
                PropertyAccessors accessors = property.GetAccessors();
                bool hasPublicGetter = !accessors.Getter.IsNil && (reader.GetMethodDefinition(accessors.Getter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                bool hasPublicSetter = !accessors.Setter.IsNil && (reader.GetMethodDefinition(accessors.Setter).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                if (hasPublicGetter || hasPublicSetter)
                {
                    string accessorText = hasPublicGetter
                        ? (hasPublicSetter ? "{ get; set; }" : "{ get; }")
                        : "{ set; }";
                    members.Add(new WinMdMemberInfo
                    {
                        Name = name,
                        Kind = MemberKind.Property,
                        Signature = $"{returnType} {name} {accessorText}",
                        ReturnType = returnType,
                        DeprecatedMessage = deprecated
                    });
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
                or InvalidOperationException or ArgumentException)
            {
                // Signature blob we cannot decode — still list the property by name.
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Property,
                    Signature = "/* type not decodable */ " + name,
                    DeprecatedMessage = deprecated
                });
            }
        }

        foreach (EventDefinitionHandle eventHandle in typeDef.GetEvents())
        {
            EventDefinition @event = reader.GetEventDefinition(eventHandle);
            string name = reader.GetString(@event.Name);
            EventAccessors accessors = @event.GetAccessors();
            bool isPublic = !accessors.Adder.IsNil && (reader.GetMethodDefinition(accessors.Adder).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            if (!isPublic && !accessors.Remover.IsNil && (reader.GetMethodDefinition(accessors.Remover).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
            {
                isPublic = true;
            }
            if (isPublic)
            {
                string handlerType = GetHandleTypeName(reader, @event.Type);
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Event,
                    Signature = "event " + handlerType + " " + name,
                    ReturnType = handlerType,
                    DeprecatedMessage = GetDeprecatedMessage(reader, @event.GetCustomAttributes())
                });
            }
        }

        return members;
    }

    /// <summary>
    /// Pairs each signature parameter type with its metadata Parameter row.
    /// <para>
    /// Rows are matched by <see cref="Parameter.SequenceNumber"/> rather than by
    /// enumeration order: a parameter with no row of its own is simply absent from the
    /// table, so walking the rows in order would shift every name after the gap onto
    /// the wrong parameter. SequenceNumber is 1-based, with 0 reserved for the return
    /// value.
    /// </para>
    /// </summary>
    private static List<WinMdParameterInfo> GetMethodParameters(MetadataReader reader, MethodDefinition method, MethodSignature<string> sig)
    {
        var parameters = new List<WinMdParameterInfo>();
        var rows = new Dictionary<int, Parameter>();
        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber > 0)
            {
                rows[parameter.SequenceNumber] = parameter;
            }
        }
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            bool hasRow = rows.TryGetValue(i + 1, out Parameter row);
            string? rowName = hasRow ? reader.GetString(row.Name) : null;
            parameters.Add(new WinMdParameterInfo
            {
                Name = string.IsNullOrEmpty(rowName) ? $"arg{i}" : rowName,
                Type = ApplyParameterDirection(sig.ParameterTypes[i], hasRow ? row.Attributes : default)
            });
        }
        return parameters;
    }

    /// <summary>
    /// Renders a by-reference parameter with the keyword C# actually requires.
    /// <para>
    /// The signature blob only says "by reference"; whether that is <c>out</c>,
    /// <c>in</c>, or <c>ref</c> lives in the Parameter row's attributes. Reporting every
    /// one as <c>ref</c> means a caller copying the signature of a Try-pattern API such
    /// as <c>Boolean TryGetValue(String key, out String value)</c> writes code that does
    /// not compile.
    /// </para>
    /// </summary>
    private static string ApplyParameterDirection(string signatureType, ParameterAttributes attributes)
    {
        const string ByRefPrefix = "ref ";
        if (!signatureType.StartsWith(ByRefPrefix, StringComparison.Ordinal))
        {
            return signatureType;
        }
        string elementType = signatureType.Substring(ByRefPrefix.Length);
        bool isOut = (attributes & ParameterAttributes.Out) != 0;
        bool isIn = (attributes & ParameterAttributes.In) != 0;
        // [in, out] together is a genuinely read-write reference, i.e. plain 'ref'.
        string keyword = isOut && !isIn ? "out" : (isIn && !isOut ? "in" : "ref");
        return keyword + " " + elementType;
    }

    internal static List<string> ParseEnumValues(MetadataReader reader, TypeDefinition typeDef)
    {
        return typeDef.GetFields()
            .Select(reader.GetFieldDefinition)
            .Where(field => (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public
                && (field.Attributes & FieldAttributes.Static) != FieldAttributes.PrivateScope)
            .Select(field => reader.GetString(field.Name))
            .Where(name => name != "value__")
            .ToList();
    }

    private static string GetHandleTypeName(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeDefName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeRefName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => DecodeTypeSpecification(reader, (TypeSpecificationHandle)handle),
            _ => "unknown",
        };
    }

    private static string DecodeTypeSpecification(MetadataReader reader, TypeSpecificationHandle handle)
    {
        try
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(new SimpleTypeProvider(), null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
            or InvalidOperationException or ArgumentException)
        {
            // An undecodable type spec renders as "unknown" rather than failing the type.
            return "unknown";
        }
    }

    private static string? GetDeprecatedMessage(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (var attrHandle in attributes)
        {
            try
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                string? attrName = GetCustomAttributeName(reader, attr);
                if (attrName != null &&
                    (attrName.Equals("DeprecatedAttribute", StringComparison.Ordinal) ||
                     attrName.Equals("ObsoleteAttribute", StringComparison.Ordinal)))
                {
                    return DecodeDeprecatedMessage(reader, attr);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
                or InvalidOperationException or ArgumentException)
            {
                // An attribute we cannot read tells us nothing about deprecation;
                // keep scanning the remaining attributes.
            }
        }
        return null;
    }

    private static string? GetCustomAttributeName(MetadataReader reader, CustomAttribute attr)
    {
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent).Name);
            }
        }
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
            var declaringType = reader.GetTypeDefinition(methodDef.GetDeclaringType());
            return reader.GetString(declaringType.Name);
        }
        return null;
    }

    private static string? DecodeDeprecatedMessage(MetadataReader reader, CustomAttribute attr)
    {
        try
        {
            var value = attr.DecodeValue(new CustomAttributeTypeProvider());
            if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string msg && !string.IsNullOrEmpty(msg))
            {
                return msg;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException
            or InvalidOperationException or ArgumentException)
        {
            // The attribute is present but its argument blob is undecodable, so fall
            // through to the generic message below — the API is still deprecated.
        }
        return "This API is deprecated.";
    }
}
