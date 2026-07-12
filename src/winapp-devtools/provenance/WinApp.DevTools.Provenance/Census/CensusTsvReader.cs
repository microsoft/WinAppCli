using System.Globalization;

namespace WinApp.DevTools.Provenance.Census;

/// <summary>
/// A parsed census TSV: the raw element rows plus the build config the file name identified.
/// </summary>
/// <param name="Config">The build config this file was collected under.</param>
/// <param name="Page">The fixture page label (the part of the file name after the config).</param>
/// <param name="FileName">The source TSV file name (for the per-page coverage report).</param>
/// <param name="Elements">The parsed element rows.</param>
public sealed record CensusTsvFile(
    CensusConfig Config,
    string Page,
    string FileName,
    IReadOnlyList<TapElement> Elements);

/// <summary>
/// Reads raw census TSVs (<c>handle, type, name, file, line, col</c>) emitted by the "reading the
/// UI" collector. File names follow <c>&lt;config&gt;-&lt;page&gt;.tsv</c>; the config is resolved by
/// longest-matching known label so <c>release-nolineinfo-*</c> is not mistaken for <c>release-*</c>.
/// Pure I/O + parsing — no grading (that is the aggregator's job).
/// </summary>
public static class CensusTsvReader
{
    private const string ExpectedHeader = "handle\ttype\tname\tfile\tline\tcol";

    /// <summary>Reads and parses every <c>*.tsv</c> in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<CensusTsvFile> ReadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Census TSV directory not found: {directory}");
        }

        List<CensusTsvFile> files = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.tsv").OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(ReadFile(path));
        }

        return files;
    }

    /// <summary>Reads and parses a single census TSV file.</summary>
    public static CensusTsvFile ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string name = Path.GetFileNameWithoutExtension(path);
        (CensusConfig config, string page) = SplitConfigAndPage(name);

        List<TapElement> elements = [];
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || IsHeader(line))
            {
                continue;
            }

            if (TryParseRow(line, out TapElement element))
            {
                elements.Add(element);
            }
        }

        return new CensusTsvFile(config, page, Path.GetFileName(path), elements);
    }

    /// <summary>
    /// Splits a <c>&lt;config&gt;-&lt;page&gt;</c> file stem into its config (by longest known-label
    /// prefix) and page. An unknown prefix yields a neutral config labelled with the first segment.
    /// </summary>
    public static (CensusConfig Config, string Page) SplitConfigAndPage(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        foreach (CensusConfig config in CensusConfig.Known)
        {
            string prefix = config.Label + "-";
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (config, stem[prefix.Length..]);
            }
        }

        int dash = stem.IndexOf('-', StringComparison.Ordinal);
        return dash > 0
            ? (CensusConfig.FromLabel(stem[..dash]), stem[(dash + 1)..])
            : (CensusConfig.FromLabel(stem), string.Empty);
    }

    private static bool IsHeader(string line) =>
        line.StartsWith("handle\t", StringComparison.OrdinalIgnoreCase)
        || line.Equals(ExpectedHeader, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseRow(string line, out TapElement element)
    {
        element = default!;
        string[] parts = line.Split('\t');
        if (parts.Length < 6)
        {
            return false;
        }

        long handle = long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long h) ? h : 0;
        int lineNo = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 0;
        int col = int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0;

        element = new TapElement(handle, parts[1].Trim(), parts[2].Trim(), parts[3].Trim(), lineNo, col);
        return true;
    }
}
