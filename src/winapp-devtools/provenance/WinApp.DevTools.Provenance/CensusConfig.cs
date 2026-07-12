namespace WinApp.DevTools.Provenance;

/// <summary>
/// A build configuration the census probes. The four normative configs are Debug, Release,
/// Packaged and Trimmed (provenance spec §5); <see cref="ReleaseNoLineInfo"/> is an extra
/// diagnostic probe (Release with XAML line-info explicitly disabled) that surfaces the
/// worst-case "line-info stripped" behaviour.
/// </summary>
/// <param name="Label">Stable lowercase label used in TSV file names and reports.</param>
/// <param name="StripsLineInfo">Whether this config is expected to strip XAML line-info.</param>
/// <param name="IsReleaseFamily">Whether this is a Release-style (optimized) build.</param>
public sealed record CensusConfig(string Label, bool StripsLineInfo, bool IsReleaseFamily)
{
    /// <summary>Debug: the upper bound (line-info intact).</summary>
    public static readonly CensusConfig Debug = new("debug", StripsLineInfo: false, IsReleaseFamily: false);

    /// <summary>Release: the honest field case and the primary Gate-1 config.</summary>
    public static readonly CensusConfig Release = new("release", StripsLineInfo: false, IsReleaseFamily: true);

    /// <summary>Release with XAML line-info disabled: the "stripped" worst case.</summary>
    public static readonly CensusConfig ReleaseNoLineInfo = new("release-nolineinfo", StripsLineInfo: true, IsReleaseFamily: true);

    /// <summary>MSIX-installed reality.</summary>
    public static readonly CensusConfig Packaged = new("packaged", StripsLineInfo: false, IsReleaseFamily: true);

    /// <summary>Trimmed / self-contained: worst case for metadata survival.</summary>
    public static readonly CensusConfig Trimmed = new("trimmed", StripsLineInfo: true, IsReleaseFamily: true);

    /// <summary>All configs the census understands, longest label first (for prefix matching).</summary>
    public static readonly IReadOnlyList<CensusConfig> Known =
        [ReleaseNoLineInfo, Debug, Release, Packaged, Trimmed];

    /// <summary>Resolves a known config by label, or fabricates a neutral one for an unknown label.</summary>
    public static CensusConfig FromLabel(string label)
    {
        foreach (CensusConfig c in Known)
        {
            if (string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return new CensusConfig(label, StripsLineInfo: false, IsReleaseFamily: false);
    }
}
