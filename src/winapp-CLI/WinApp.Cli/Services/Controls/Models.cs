// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.Json.Serialization;

[JsonSerializable(typeof(Scenario[]))]
[JsonSerializable(typeof(CorePattern[]))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(DocLink[]))]
internal partial class ControlsJsonContext : JsonSerializerContext { }

internal sealed class Scenario
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("controlId")] public string ControlId { get; set; } = "";
    [JsonPropertyName("controlName")] public string ControlName { get; set; } = "";
    [JsonPropertyName("headerText")] public string HeaderText { get; set; } = "";
    [JsonPropertyName("xaml")] public string? Xaml { get; set; }
    [JsonPropertyName("csharp")] public string? CSharp { get; set; }
    /// <summary>"gallery" or "toolkit". Drives id prefix and metadata output.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "gallery";
    /// <summary>NuGet package required to use this control (toolkit only).</summary>
    [JsonPropertyName("nugetPackage")] public string? NuGetPackage { get; set; }
    /// <summary>XAML namespace declarations needed (e.g., xmlns:controls="...") (toolkit only).</summary>
    [JsonPropertyName("xmlnsImports")] public string[] XmlnsImports { get; set; } = [];
    /// <summary>Longer description from ControlInfoData.json (Gallery only).</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Control-level one-line concept summary. For gallery: ControlInfoData.Subtitle
    /// (short, median 68 chars). For toolkit: md frontmatter description. Surfaced in search
    /// list as "[gallery] Name — &lt;summary&gt;".</summary>
    [JsonPropertyName("controlDescription")] public string? ControlDescription { get; set; }
    /// <summary>Related WinUI 3 controls — names of "see also" alternatives/pairings (Gallery only).</summary>
    [JsonPropertyName("relatedControls")] public string[] RelatedControls { get; set; } = [];
    /// <summary>API namespace from ControlInfoData.json (Gallery only). Surfaced in output
    /// only when non-default — helps agents pick the right `using`/`xmlns` for long-tail
    /// controls in Microsoft.Windows.*, Microsoft.UI.Windowing, etc. Empty when unknown
    /// or when the standard Microsoft.UI.Xaml.Controls namespace is enough.</summary>
    [JsonPropertyName("apiNamespace")] public string? ApiNamespace { get; set; }
    /// <summary>Official documentation links (API reference, guidelines, etc.) (Gallery only).</summary>
    [JsonPropertyName("docs")] public DocLink[] Docs { get; set; } = [];
}

internal sealed class DocLink
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
}

internal sealed class CorePattern
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("prerequisites")] public string[] Prerequisites { get; set; } = [];
    [JsonPropertyName("xaml")] public string? Xaml { get; set; }
    [JsonPropertyName("csharp")] public string CSharp { get; set; } = "";
    [JsonPropertyName("notes")] public string[] Notes { get; set; } = [];
}
