// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Gallery;


[JsonSerializable(typeof(Scenario[]))]
[JsonSerializable(typeof(CorePattern[]))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal partial class GalleryJsonContext : JsonSerializerContext { }

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
