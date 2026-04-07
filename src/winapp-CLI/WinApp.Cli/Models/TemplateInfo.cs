// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

/// <summary>
/// Represents a discovered dotnet template from a template package.
/// </summary>
internal record TemplateInfo(
    string Name,
    string ShortName,
    string Description,
    TemplateType Type,
    string Language,
    IReadOnlyList<TemplateParameter> Parameters);

/// <summary>
/// A user-facing parameter defined in a template's template.json symbols section.
/// Only includes symbols with type "parameter" (excludes derived/generated/bind/computed).
/// </summary>
internal record TemplateParameter(
    string Name,
    string? Description,
    TemplateParameterDataType DataType,
    string? DefaultValue,
    IReadOnlyList<TemplateChoice>? Choices);

/// <summary>
/// A selectable choice for a "choice"-typed template parameter.
/// </summary>
internal record TemplateChoice(string Value, string? Description);

internal enum TemplateType
{
    Project,
    Item
}

internal enum TemplateParameterDataType
{
    Text,
    Choice,
    Bool
}

/// <summary>
/// Raw template.json model for deserialization.
/// Maps to the .NET template engine schema: https://json.schemastore.org/template
/// </summary>
internal class TemplateJson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    public TemplateJsonTags? Tags { get; set; }

    [JsonPropertyName("symbols")]
    public Dictionary<string, TemplateJsonSymbol>? Symbols { get; set; }
}

internal class TemplateJsonTags
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

internal class TemplateJsonSymbol
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("datatype")]
    public string? DataType { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("choices")]
    public List<TemplateJsonChoice>? Choices { get; set; }
}

internal class TemplateJsonChoice
{
    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

[JsonSerializable(typeof(TemplateJson))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TemplateJsonContext : JsonSerializerContext
{
}
