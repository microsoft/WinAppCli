using System.Globalization;

namespace WinApp.DevTools.Provenance.Census;

/// <summary>
/// Applies the Gate-1 kill-criteria (provenance spec §5) to an aggregated census. The criteria are,
/// judged on the Release config:
/// <list type="bullet">
///   <item>≥ <see cref="SourceBackedFloorPct"/>% of source-backed elements resolve to an exact span;</item>
///   <item>≥ <see cref="TemplatedFloorPct"/>% of generated elements map to a template/style def;</item>
///   <item><b>0%</b> false-confident answers — any single one is an automatic KILL.</item>
/// </list>
/// The false-confident rule dominates; a Release config that resolves 0% of source-backed elements
/// is also a KILL (select-to-source does not work at all).
/// </summary>
public static class Gate1Evaluator
{
    /// <summary>Minimum share of source-backed elements that must resolve to an exact span in Release.</summary>
    public const double SourceBackedFloorPct = 70d;

    /// <summary>Minimum share of generated elements that must map to a template/style def in Release.</summary>
    public const double TemplatedFloorPct = 40d;

    /// <summary>Evaluates the Gate-1 criteria against an aggregated census.</summary>
    public static Gate1Report Evaluate(CensusResult census)
    {
        ArgumentNullException.ThrowIfNull(census);

        int falseConfidentTotal = census.Configs.Sum(static c => c.FalseConfident);
        ConfigCensus? release = PickReleaseConfig(census);
        List<string> reasons = [];

        // 1. The false-confident prohibition dominates every other signal.
        if (falseConfidentTotal > 0)
        {
            IEnumerable<string> offenders = census.Configs
                .Where(static c => c.FalseConfident > 0)
                .Select(static c => $"{c.Config.Label}={c.FalseConfident}");
            reasons.Add($"KILL: {falseConfidentTotal} false-confident answer(s) ({string.Join(", ", offenders)}); the kill-criteria require exactly 0%.");
            return new Gate1Report
            {
                Verdict = Gate1Verdict.Kill,
                Reasons = reasons,
                EvaluatedConfigLabel = release?.Config.Label,
                SourceBackedResolvedPct = release?.SourceBackedResolvedPct ?? 0d,
                TemplatedToTemplatePct = release?.TemplatedToTemplatePct ?? 0d,
                FalseConfidentTotal = falseConfidentTotal,
            };
        }

        // 2. Without a non-stripping Release-family config there is nothing to judge Gate-1 on.
        if (release is null)
        {
            reasons.Add("INCONCLUSIVE: no non-stripping Release-family config was measured; run the Release census to decide Gate-1.");
            return new Gate1Report
            {
                Verdict = Gate1Verdict.Inconclusive,
                Reasons = reasons,
                FalseConfidentTotal = 0,
            };
        }

        double sb = release.SourceBackedResolvedPct;
        double tt = release.TemplatedToTemplatePct;
        bool sourceBackedPass = sb >= SourceBackedFloorPct;
        bool templatedPass = tt >= TemplatedFloorPct;

        // 3. Select-to-source resolving 0% of real source-backed elements in Release is a hard failure.
        if (release.SourceBackedTotal > 0 && sb <= 0d)
        {
            reasons.Add($"KILL: 0% of {release.SourceBackedTotal} source-backed elements resolved to a line in '{release.Config.Label}'; select-to-source does not work in Release.");
            return new Gate1Report
            {
                Verdict = Gate1Verdict.Kill,
                Reasons = reasons,
                EvaluatedConfigLabel = release.Config.Label,
                SourceBackedResolvedPct = sb,
                TemplatedToTemplatePct = tt,
                FalseConfidentTotal = 0,
            };
        }

        // 4. Both floors met and no false-confident answers -> GO; otherwise CONDITIONAL.
        Gate1Verdict verdict;
        if (sourceBackedPass && templatedPass)
        {
            verdict = Gate1Verdict.Go;
            reasons.Add($"GO: source-backed resolved {Fmt(sb)}% ≥ {Fmt(SourceBackedFloorPct)}% and templated-to-template {Fmt(tt)}% ≥ {Fmt(TemplatedFloorPct)}% in '{release.Config.Label}', 0 false-confident.");
        }
        else
        {
            verdict = Gate1Verdict.Conditional;
            if (!sourceBackedPass)
            {
                reasons.Add($"CONDITIONAL: source-backed resolved {Fmt(sb)}% < {Fmt(SourceBackedFloorPct)}% floor in '{release.Config.Label}'.");
            }

            if (!templatedPass)
            {
                reasons.Add($"CONDITIONAL: templated-to-template {Fmt(tt)}% < {Fmt(TemplatedFloorPct)}% floor in '{release.Config.Label}'.");
            }

            if (release.SourceBackedTotal == 0)
            {
                reasons.Add($"CONDITIONAL: no source-backed elements were present in '{release.Config.Label}' to measure resolution.");
            }
        }

        return new Gate1Report
        {
            Verdict = verdict,
            Reasons = reasons,
            EvaluatedConfigLabel = release.Config.Label,
            SourceBackedResolvedPct = sb,
            TemplatedToTemplatePct = tt,
            FalseConfidentTotal = 0,
        };
    }

    /// <summary>
    /// Picks the config Gate-1 judges: a Release-family config that does NOT strip line-info,
    /// preferring the standard <c>release</c>, then <c>packaged</c>, then any such config. The
    /// diagnostic line-info-stripped probe is deliberately never the arbiter.
    /// </summary>
    public static ConfigCensus? PickReleaseConfig(CensusResult census)
    {
        ArgumentNullException.ThrowIfNull(census);
        List<ConfigCensus> candidates = census.Configs
            .Where(static c => c.Config.IsReleaseFamily && !c.Config.StripsLineInfo)
            .ToList();

        return candidates.FirstOrDefault(static c => string.Equals(c.Config.Label, "release", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(static c => string.Equals(c.Config.Label, "packaged", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static string Fmt(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
