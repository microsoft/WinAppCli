// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Net.Http;
using System.Text.Json;

/// <summary>
/// microsoft-ui-reactor ReactorGallery scenarios. Reads the purpose-built
/// <c>reactor-search-index.json</c> (a schema the Reactor team owns) and maps it
/// onto the internal <see cref="Scenario"/> model. Reactor samples are C#-only
/// declarative WinUI (<c>UseMemo</c>, <c>DataGrid(...)</c>, <c>Column&lt;T&gt;(...)</c>) —
/// kept verbatim, never run through the gallery/toolkit sample cleaners. There is
/// no XAML, so <see cref="Scenario.Xaml"/> stays null.
///
/// Each control's curated <c>keywords</c> become the 3.0-weighted enrichment tag
/// field (<see cref="ProviderData.Tags"/>); they are served VERBATIM — not
/// stop-word cleaned — so multi-word intent terms like "css layout" survive
/// (cleaning would drop the TagOnly stop word "layout" and break searches such as
/// "flex layout"). The controls that declare control-level <c>usings</c>
/// (data-grid, docking, flex, property-grid) get those folded into each sample's
/// C# so the emitted snippet compiles standalone.
///
/// Unlike upstream winui-search, find-ui embeds no offline snapshot: the index is
/// always fetched from GitHub and cached per-user, so the curated keyword tags
/// ride along with the fetch rather than a baked-in <c>reactor-tags.json</c>.
/// </summary>
internal static class ReactorFetcher
{
    private const string IndexUrl =
        "https://raw.githubusercontent.com/microsoft/microsoft-ui-reactor/main/samples/ReactorGallery/reactor-search-index.json";

    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "winapp-find-ui/1.0" } },
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Fetch fresh scenarios + tags from GitHub. Reactor C# and curated
    /// keywords are kept verbatim. The body is streamed through the shared
    /// byte-capped helper so an accidentally-huge upstream file can't exhaust
    /// memory.</summary>
    internal static async Task<(Scenario[] scenarios, Dictionary<string, string[]> tags)> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await ControlsHttpHelper.GetStringCappedAsync(Http, IndexUrl, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Map the <c>reactor-search-index.json</c> document to
    /// <see cref="Scenario"/>[] + the per-control tag dictionary. Parsed with
    /// <see cref="JsonDocument"/> (AOT-safe, no reflection).</summary>
    internal static (Scenario[] scenarios, Dictionary<string, string[]> tags) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var scenarios = new List<Scenario>();
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("controls", out var controls)
            || controls.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<Scenario>(), tags);
        }

        foreach (var control in controls.EnumerateArray())
        {
            var controlId = GetString(control, "id");
            if (string.IsNullOrEmpty(controlId)) continue;

            var controlName = GetString(control, "name");
            var controlDescription = GetString(control, "description");
            var apiNamespace = GetString(control, "apiNamespace");
            var nugetPackage = GetString(control, "nugetPackage");
            var relatedControls = GetStringArray(control, "relatedControls");
            var usings = GetStringArray(control, "usings");
            var keywords = GetStringArray(control, "keywords");

            // Curated keywords -> 3.0-weighted enrichment tag field, verbatim.
            if (keywords.Length > 0) tags[controlId] = keywords;

            // For controls that declare usings, prepend `using X;` lines + a blank
            // line to EACH sample's code so the declarative snippet compiles alone.
            var usingsPrefix = usings.Length > 0
                ? string.Concat(usings.Select(u => $"using {u};\n")) + "\n"
                : "";

            if (!control.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                var header = GetString(sample, "header");
                var code = GetString(sample, "code");

                // Reactor samples are C#-only (Xaml is always null), so a sample
                // with no code has no usable content — skip it rather than emit a
                // blank scenario. Guard on the raw code (before the usings prefix)
                // so a control that only has control-level usings can't slip
                // through as a using-only stub. Mirrors GalleryFetcher's
                // "csharp == null && xaml == null -> continue" rule.
                if (string.IsNullOrWhiteSpace(code)) continue;

                index++;
                scenarios.Add(new Scenario
                {
                    Id = $"{controlId}-{index}",
                    ControlId = controlId,
                    ControlName = controlName,
                    HeaderText = header,
                    Xaml = null,
                    CSharp = usingsPrefix + code,
                    Source = "reactor",
                    NuGetPackage = string.IsNullOrEmpty(nugetPackage) ? null : nugetPackage,
                    ApiNamespace = string.IsNullOrEmpty(apiNamespace) ? null : apiNamespace,
                    ControlDescription = string.IsNullOrEmpty(controlDescription) ? null : controlDescription,
                    RelatedControls = relatedControls,
                });
            }
        }

        return (scenarios.ToArray(), tags);
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string[] GetStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list.ToArray();
    }
}
