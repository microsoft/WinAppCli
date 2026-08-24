// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Property names and version of the shared WinUI sample index contract described by
/// <c>docs/winui-sample-index.schema.json</c>. Declared once here so the reader
/// (<see cref="SampleIndexParser"/>), the writer (<see cref="SampleIndexWriter"/>) and the
/// published schema cannot drift apart — <c>SampleIndexTests</c> asserts that the schema
/// file declares exactly these names.
///
/// The names are deliberately those already published by
/// <c>microsoft/microsoft-ui-reactor</c> in <c>reactor-search-index.json</c>: that index is
/// the prior art this contract generalizes, and keeping its vocabulary means it stays valid
/// against this schema without the Reactor team changing anything.
/// </summary>
internal static class SampleIndexSchema
{
    /// <summary>Contract version consumers accept. Bump only on a breaking shape change.</summary>
    public const int Version = 1;

    // Document level.
    public const string SchemaVersion = "schemaVersion";
    public const string Source = "source";
    public const string GeneratedAtUtc = "generatedAtUtc";
    public const string Controls = "controls";

    // Control level.
    public const string Id = "id";
    public const string Name = "name";

    /// <summary>One-line summary. Maps to <see cref="Scenario.ControlDescription"/>.</summary>
    public const string Description = "description";

    /// <summary>Long-form prose. Maps to <see cref="Scenario.Description"/>. Distinct from
    /// <see cref="Description"/> because Gallery maintains both a subtitle and a full
    /// description, and <see cref="Description"/> is spoken for by Reactor's existing file.</summary>
    public const string Details = "details";

    public const string ApiNamespace = "apiNamespace";
    public const string NuGetPackage = "nugetPackage";
    public const string RelatedControls = "relatedControls";
    public const string XmlnsImports = "xmlnsImports";
    public const string Usings = "usings";
    public const string Keywords = "keywords";
    public const string Docs = "docs";
    public const string Samples = "samples";

    // Doc link level.
    public const string Title = "title";
    public const string Uri = "uri";

    // Sample level.
    public const string Header = "header";
    public const string Xaml = "xaml";
    public const string Code = "code";
    public const string Language = "language";

    /// <summary>Every document-level property the contract defines.</summary>
    public static readonly string[] DocumentProperties =
        [SchemaVersion, Source, GeneratedAtUtc, Controls];

    /// <summary>Every control-level property the contract defines.</summary>
    public static readonly string[] ControlProperties =
        [Id, Name, Description, Details, ApiNamespace, NuGetPackage, RelatedControls, XmlnsImports, Usings, Keywords, Docs, Samples];

    /// <summary>Every doc-link property the contract defines.</summary>
    public static readonly string[] DocLinkProperties = [Title, Uri];

    /// <summary>Every sample-level property the contract defines.</summary>
    public static readonly string[] SampleProperties = [Header, Xaml, Code, Language];
}
