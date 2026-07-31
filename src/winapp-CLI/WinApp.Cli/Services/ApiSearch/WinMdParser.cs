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
    public static List<WinMdTypeInfo> ParseFile(string filePath)
    {
        var results = new List<WinMdTypeInfo>();
        try
        {
            using FileStream peStream = File.OpenRead(filePath);
            using var peReader = new PEReader(peStream);
            if (!peReader.HasMetadata)
            {
                return results;
            }
            MetadataReader reader = peReader.GetMetadataReader();
            var typeProvider = new SimpleTypeProvider();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition typeDef = reader.GetTypeDefinition(handle);
                string name = reader.GetString(typeDef.Name);
                string ns = reader.GetString(typeDef.Namespace);
                if (ShouldSkipType(name, typeDef))
                {
                    continue;
                }

                TypeKind typeKind = DetermineTypeKind(reader, typeDef);
                string? baseTypeName = GetBaseTypeName(reader, typeDef);
                List<WinMdMemberInfo> members = ParseMembers(reader, typeDef, typeProvider);
                List<string>? enumValues = typeKind == TypeKind.Enum ? ParseEnumValues(reader, typeDef) : null;
                string fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                string? deprecatedMessage = GetDeprecatedMessage(reader, typeDef.GetCustomAttributes());
                ApplyMemberDeprecation(reader, typeDef, members);
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
        catch
        {
            // A single unparseable metadata file must not fail the whole scan;
            // skip it silently (stderr writes would corrupt --json output).
        }
        return results;
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
            try
            {
                MethodSignature<string> sig = method.DecodeSignature(typeProvider, null);
                List<WinMdParameterInfo> parameters = GetMethodParameters(reader, method, sig);
                string paramText = string.Join(", ", parameters.Select(p => p.Type + " " + p.Name));
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Method,
                    Signature = $"{sig.ReturnType} {name}({paramText})",
                    ReturnType = sig.ReturnType,
                    Parameters = parameters
                });
            }
            catch
            {
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Method,
                    Signature = name + "(/* signature not decodable */)"
                });
            }
        }

        foreach (PropertyDefinitionHandle propertyHandle in typeDef.GetProperties())
        {
            PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
            string name = reader.GetString(property.Name);
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
                        ReturnType = returnType
                    });
                }
            }
            catch
            {
                members.Add(new WinMdMemberInfo
                {
                    Name = name,
                    Kind = MemberKind.Property,
                    Signature = "/* type not decodable */ " + name
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
                    ReturnType = handlerType
                });
            }
        }

        return members;
    }

    private static List<WinMdParameterInfo> GetMethodParameters(MetadataReader reader, MethodDefinition method, MethodSignature<string> sig)
    {
        var parameters = new List<WinMdParameterInfo>();
        var names = new List<string>();
        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber > 0)
            {
                names.Add(reader.GetString(parameter.Name));
            }
        }
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            parameters.Add(new WinMdParameterInfo
            {
                Name = i < names.Count ? names[i] : $"arg{i}",
                Type = sig.ParameterTypes[i]
            });
        }
        return parameters;
    }

    internal static List<string> ParseEnumValues(MetadataReader reader, TypeDefinition typeDef)
    {
        var values = new List<string>();
        foreach (FieldDefinitionHandle fieldHandle in typeDef.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
            string name = reader.GetString(field.Name);
            if (name != "value__"
                && (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public
                && (field.Attributes & FieldAttributes.Static) != FieldAttributes.PrivateScope)
            {
                values.Add(name);
            }
        }
        return values;
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
        catch
        {
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
            catch
            {
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
        catch
        {
        }
        return "This API is deprecated.";
    }

    private static void ApplyMemberDeprecation(MetadataReader reader, TypeDefinition typeDef, List<WinMdMemberInfo> members)
    {
        var memberByName = new Dictionary<string, List<WinMdMemberInfo>>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (!memberByName.TryGetValue(m.Name, out var list))
            {
                list = new List<WinMdMemberInfo>();
                memberByName[m.Name] = list;
            }
            list.Add(m);
        }

        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            string name = reader.GetString(method.Name);
            string? msg = GetDeprecatedMessage(reader, method.GetCustomAttributes());
            if (msg != null && memberByName.TryGetValue(name, out var matches))
            {
                foreach (var m in matches)
                {
                    m.DeprecatedMessage ??= msg;
                }
            }
        }

        foreach (var propHandle in typeDef.GetProperties())
        {
            var prop = reader.GetPropertyDefinition(propHandle);
            string name = reader.GetString(prop.Name);
            string? msg = GetDeprecatedMessage(reader, prop.GetCustomAttributes());
            if (msg != null && memberByName.TryGetValue(name, out var matches))
            {
                foreach (var m in matches)
                {
                    m.DeprecatedMessage ??= msg;
                }
            }
        }

        foreach (var eventHandle in typeDef.GetEvents())
        {
            var evt = reader.GetEventDefinition(eventHandle);
            string name = reader.GetString(evt.Name);
            string? msg = GetDeprecatedMessage(reader, evt.GetCustomAttributes());
            if (msg != null && memberByName.TryGetValue(name, out var matches))
            {
                foreach (var m in matches)
                {
                    m.DeprecatedMessage ??= msg;
                }
            }
        }
    }
}
