using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinApp.DevTools.Provenance.Census;

/// <summary>
/// Renders an aggregated census + Gate-1 verdict to the published report shapes: a human-readable
/// Markdown summary (aggregated by <see cref="SourceKind"/>) and a machine-readable JSON document.
/// Both are deterministic given the same input so they can be committed as a baseline and diffed.
/// </summary>
public static class CensusReport
{
    /// <summary>Canonical column order for the per-<see cref="SourceKind"/> breakdown.</summary>
    private static readonly SourceKind[] KindOrder =
    [
        SourceKind.SourceBacked,
        SourceKind.TemplateGenerated,
        SourceKind.StyleGenerated,
        SourceKind.BindingGenerated,
        SourceKind.RuntimeOnly,
        SourceKind.ResourceOrigin,
        SourceKind.Ambiguous,
        SourceKind.Unreachable,
    ];

    /// <summary>Renders the Markdown census report.</summary>
    public static string ToMarkdown(CensusResult census, Gate1Report gate)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(gate);

        StringBuilder sb = new();
        sb.AppendLine("# Source-resolution census (Gate 1)");
        sb.AppendLine();
        sb.Append("_Generated ").Append(census.GeneratedUtc.ToString("u", CultureInfo.InvariantCulture));
        if (census.PageLabels.Count > 0)
        {
            sb.Append(" · pages: ").Append(string.Join(", ", census.PageLabels));
        }

        sb.AppendLine("_");
        sb.AppendLine();

        sb.Append("**Verdict: ").Append(gate.Verdict.ToString().ToUpperInvariant()).Append("** — ")
          .AppendLine(string.Join(" ", gate.Reasons));
        sb.AppendLine();

        // Gate-1 summary table.
        sb.AppendLine("## Gate-1 metrics");
        sb.AppendLine();
        sb.AppendLine("| Config | Total | Source-backed→line % | Templated→template % | False-confident % |");
        sb.AppendLine("|---|--:|--:|--:|--:|");
        foreach (ConfigCensus c in census.Configs)
        {
            sb.Append("| ").Append(c.Config.Label)
              .Append(" | ").Append(c.Total.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(Pct(c.SourceBackedResolvedPct))
              .Append(" | ").Append(Pct(c.TemplatedToTemplatePct))
              .Append(" | ").Append(Pct(c.FalseConfidentPct))
              .AppendLine(" |");
        }

        sb.AppendLine();

        // Per-SourceKind breakdown.
        sb.AppendLine("## Elements by SourceKind");
        sb.AppendLine();
        sb.Append("| Config | Total |");
        foreach (SourceKind kind in KindOrder)
        {
            sb.Append(' ').Append(kind.ToWire()).Append(" |");
        }

        sb.AppendLine();
        sb.Append("|---|--:|");
        foreach (SourceKind _ in KindOrder)
        {
            sb.Append("--:|");
        }

        sb.AppendLine();
        foreach (ConfigCensus c in census.Configs)
        {
            sb.Append("| ").Append(c.Config.Label).Append(" | ").Append(c.Total.ToString(CultureInfo.InvariantCulture)).Append(" |");
            foreach (SourceKind kind in KindOrder)
            {
                sb.Append(' ').Append(c.Count(kind).ToString(CultureInfo.InvariantCulture)).Append(" |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine(
            "Grades come from the source-provenance grader (spec §4): **source-backed** = the app's own " +
            "authored page/UserControl markup (the only kind allowed an exact line); **template/style-generated** " +
            "= mapped to a control-template or theme/style definition, never the page; **runtime-only** = no " +
            "markup provenance. *Source-backed→line %* is the select-to-source floor; *Templated→template %* is " +
            "the fraction of generated elements that still map to a template/style source; *False-confident %* " +
            "must be 0.");
        sb.AppendLine();

        // Per-page coverage.
        sb.AppendLine("## Per page × config (raw)");
        sb.AppendLine();
        sb.AppendLine("| Config | Page | OK | Elements | TSV |");
        sb.AppendLine("|---|---|:--:|--:|---|");
        Dictionary<string, int> configIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < census.Configs.Count; i++)
        {
            configIndex[census.Configs[i].Config.Label] = i;
        }

        Dictionary<string, int> pageIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < census.PageLabels.Count; i++)
        {
            pageIndex[census.PageLabels[i]] = i;
        }

        IEnumerable<PageCoverage> orderedPages = census.Pages
            .OrderBy(p => configIndex.GetValueOrDefault(p.Config.Label, int.MaxValue))
            .ThenBy(p => pageIndex.GetValueOrDefault(p.Page, int.MaxValue));
        foreach (PageCoverage p in orderedPages)
        {
            sb.Append("| ").Append(p.Config.Label)
              .Append(" | ").Append(p.Page)
              .Append(" | ").Append(p.Ok ? "✅" : "❌")
              .Append(" | ").Append(p.Elements.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(p.FileName)
              .AppendLine(" |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Renders the machine-readable JSON census report.</summary>
    public static string ToJson(CensusResult census, Gate1Report gate)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(gate);

        JsonObject configs = [];
        foreach (ConfigCensus c in census.Configs)
        {
            JsonObject kinds = [];
            foreach (SourceKind kind in KindOrder)
            {
                kinds[kind.ToWire()] = c.Count(kind);
            }

            configs[c.Config.Label] = new JsonObject
            {
                ["total"] = c.Total,
                ["stripsLineInfo"] = c.Config.StripsLineInfo,
                ["releaseFamily"] = c.Config.IsReleaseFamily,
                ["sourceKind"] = kinds,
                ["sourceBackedTotal"] = c.SourceBackedTotal,
                ["sourceBackedResolved"] = c.SourceBackedResolved,
                ["sourceBackedResolvedPct"] = Round(c.SourceBackedResolvedPct),
                ["templatedResolved"] = c.TemplatedResolved,
                ["generatedTotal"] = c.GeneratedTotal,
                ["templatedToTemplatePct"] = Round(c.TemplatedToTemplatePct),
                ["falseConfident"] = c.FalseConfident,
                ["falseConfidentPct"] = Round(c.FalseConfidentPct),
            };
        }

        JsonObject root = new()
        {
            ["generatedUtc"] = census.GeneratedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["pages"] = new JsonArray([.. census.PageLabels.Select(static p => JsonValue.Create(p))]),
            ["gate1"] = new JsonObject
            {
                ["verdict"] = gate.Verdict.ToString(),
                ["evaluatedConfig"] = gate.EvaluatedConfigLabel,
                ["sourceBackedResolvedPct"] = Round(gate.SourceBackedResolvedPct),
                ["templatedToTemplatePct"] = Round(gate.TemplatedToTemplatePct),
                ["falseConfidentTotal"] = gate.FalseConfidentTotal,
                ["thresholds"] = new JsonObject
                {
                    ["sourceBackedFloorPct"] = Gate1Evaluator.SourceBackedFloorPct,
                    ["templatedFloorPct"] = Gate1Evaluator.TemplatedFloorPct,
                },
                ["reasons"] = new JsonArray([.. gate.Reasons.Select(static r => JsonValue.Create(r))]),
            },
            ["configs"] = configs,
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string Pct(double value) => Round(value).ToString("0.#", CultureInfo.InvariantCulture);

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
