// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// Writes a WinUI sample index (<c>docs/winui-sample-index.schema.json</c>) from scenarios
/// we already hold. This is the reference generator behind the upstream ask in
/// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>: rather than
/// asking WinUI-Gallery and CommunityToolkit to design and build an index, each PR can
/// arrive as "here is the file, here is the tested code that produced it, here is the
/// schema it validates against."
///
/// <para>It is also the other half of the Phase 0 proof. Round-tripping our corpus through
/// <see cref="Write"/> and back through <see cref="SampleIndexParser.Parse"/> must return
/// the same scenarios — if it doesn't, the schema is missing a field and we'd have found
/// that out after an upstream maintainer merged it.</para>
///
/// <para>Output is deterministic: controls are ordered by id and no timestamp is written
/// unless one is asked for, so re-generating against unchanged upstream data produces a
/// byte-identical file and a drift check compares content rather than noise.</para>
/// </summary>
internal static class SampleIndexWriter
{
    /// <summary>
    /// Serialize <paramref name="scenarios"/> (and their curated <paramref name="tags"/>) as
    /// an index document.
    /// </summary>
    /// <param name="scenarios">Scenarios to index. Grouped by
    /// <see cref="Scenario.ControlId"/>; control-level metadata is taken from the first
    /// scenario of each group.</param>
    /// <param name="tags">Curated per-control search keywords, keyed by control id.</param>
    /// <param name="source">Value for the document's <c>source</c> field. Defaults to the
    /// <see cref="Scenario.Source"/> of the first scenario.</param>
    /// <param name="generatedAtUtc">Optional provenance stamp. Omitted by default because a
    /// timestamp makes every regeneration a diff, which defeats drift detection.</param>
    internal static string Write(
        IEnumerable<Scenario> scenarios,
        IReadOnlyDictionary<string, string[]>? tags = null,
        string? source = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var ordered = scenarios
            .GroupBy(s => s.ControlId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // The output is a JSON file read by JSON parsers, never interpolated into HTML
            // or script. Default escaping would render every '<' in a XAML sample as
            // \u003C, which makes the artifact unreviewable — and being reviewable as a
            // diff is the point of publishing it. Quotes, backslashes and control
            // characters are still escaped, and consumers keep ScenarioSanitizer as the
            // trust boundary on read.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(SampleIndexSchema.SchemaVersion, SampleIndexSchema.Version);

            var sourceId = source ?? ordered.FirstOrDefault()?.First().Source;
            if (!string.IsNullOrEmpty(sourceId)) writer.WriteString(SampleIndexSchema.Source, sourceId);

            if (generatedAtUtc is { } stamp)
            {
                writer.WriteString(SampleIndexSchema.GeneratedAtUtc, stamp.UtcDateTime.ToString("O"));
            }

            writer.WriteStartArray(SampleIndexSchema.Controls);
            foreach (var group in ordered)
            {
                WriteControl(writer, group.Key, [.. group], tags);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteControl(
        Utf8JsonWriter writer,
        string controlId,
        IReadOnlyList<Scenario> group,
        IReadOnlyDictionary<string, string[]>? tags)
    {
        var first = group[0];

        writer.WriteStartObject();
        writer.WriteString(SampleIndexSchema.Id, controlId);
        WriteIfPresent(writer, SampleIndexSchema.Name, first.ControlName);
        WriteIfPresent(writer, SampleIndexSchema.Description, first.ControlDescription);
        WriteIfPresent(writer, SampleIndexSchema.Details, first.Description);
        WriteIfPresent(writer, SampleIndexSchema.ApiNamespace, first.ApiNamespace);
        WriteIfPresent(writer, SampleIndexSchema.NuGetPackage, first.NuGetPackage);
        WriteIfPresent(writer, SampleIndexSchema.RelatedControls, first.RelatedControls);
        WriteIfPresent(writer, SampleIndexSchema.XmlnsImports, first.XmlnsImports);

        // No "usings" is emitted: control-level usings have already been folded into each
        // scenario's C# by the time we hold it, and emitting them as well would make a
        // reader prepend them a second time.

        if (tags is not null && tags.TryGetValue(controlId, out var keywords))
        {
            WriteIfPresent(writer, SampleIndexSchema.Keywords, keywords);
        }

        if (first.Docs.Length > 0)
        {
            writer.WriteStartArray(SampleIndexSchema.Docs);
            foreach (var doc in first.Docs)
            {
                writer.WriteStartObject();
                WriteIfPresent(writer, SampleIndexSchema.Title, doc.Title);
                writer.WriteString(SampleIndexSchema.Uri, doc.Uri);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteStartArray(SampleIndexSchema.Samples);
        foreach (var scenario in group)
        {
            writer.WriteStartObject();
            WriteIfPresent(writer, SampleIndexSchema.Header, scenario.HeaderText);
            WriteIfPresent(writer, SampleIndexSchema.Xaml, scenario.Xaml);
            WriteIfPresent(writer, SampleIndexSchema.Code, scenario.CSharp);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) writer.WriteString(name, value);
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string[] values)
    {
        if (values.Length == 0) return;

        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
