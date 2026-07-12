using System.Globalization;
using WinApp.DevTools.Provenance;
using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.CensusCli;

/// <summary>
/// Thin CLI over the provenance analysis library. It never touches a live app: it reads a directory
/// of already-collected census TSVs, grades + aggregates them, evaluates the Gate-1 criteria, and
/// writes the Markdown + JSON report. The exit code reflects the verdict so CI and the harness can
/// gate on it.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 64;
    private const int ExitKill = 2;
    private const int ExitInconclusive = 3;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage(Console.Out);
            return args.Length == 0 ? ExitUsage : 0;
        }

        if (!string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage(Console.Error);
            return ExitUsage;
        }

        try
        {
            return RunAnalyze(args[1..]);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitUsage;
        }
    }

    private static int RunAnalyze(string[] args)
    {
        string? tsvDir = null;
        string? outDir = null;
        string? mdPath = null;
        string? jsonPath = null;
        bool quiet = false;
        bool strict = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--out":
                    outDir = RequireValue(args, ref i, arg);
                    break;
                case "--md":
                    mdPath = RequireValue(args, ref i, arg);
                    break;
                case "--json":
                    jsonPath = RequireValue(args, ref i, arg);
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"unknown option '{arg}'");
                    }

                    tsvDir ??= arg;
                    break;
            }
        }

        if (tsvDir is null)
        {
            throw new ArgumentException("missing <tsvDir> argument");
        }

        IReadOnlyList<CensusTsvFile> files = CensusTsvReader.ReadDirectory(tsvDir);
        if (files.Count == 0)
        {
            throw new ArgumentException($"no .tsv files found in '{tsvDir}'");
        }

        CensusResult census = new CensusAggregator().Aggregate(files);
        Gate1Report gate = Gate1Evaluator.Evaluate(census);

        string markdown = CensusReport.ToMarkdown(census, gate);
        string json = CensusReport.ToJson(census, gate);

        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            mdPath ??= Path.Combine(outDir, "census-latest.md");
            jsonPath ??= Path.Combine(outDir, "census-latest.json");
        }

        if (mdPath is not null)
        {
            File.WriteAllText(mdPath, markdown);
        }

        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, json);
        }

        if (!quiet)
        {
            Console.Out.Write(markdown);
        }

        Console.Error.WriteLine(
            $"verdict={gate.Verdict.ToString().ToUpperInvariant()} " +
            $"config={gate.EvaluatedConfigLabel ?? "-"} " +
            $"sourceBacked={gate.SourceBackedResolvedPct.ToString("0.#", CultureInfo.InvariantCulture)}% " +
            $"templated={gate.TemplatedToTemplatePct.ToString("0.#", CultureInfo.InvariantCulture)}% " +
            $"falseConfident={gate.FalseConfidentTotal}");

        return gate.Verdict switch
        {
            Gate1Verdict.Kill => ExitKill,
            Gate1Verdict.Inconclusive => strict ? ExitInconclusive : 0,
            Gate1Verdict.Conditional => strict ? ExitInconclusive : 0,
            _ => 0,
        };
    }

    private static string RequireValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"option '{option}' requires a value");
        }

        return args[++i];
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help" or "/?";

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("winapp-provenance-census — analyze source-resolution census TSVs (Gate 1)");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  winapp-provenance-census analyze <tsvDir> [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --out <dir>     Write census-latest.md and census-latest.json into <dir>.");
        writer.WriteLine("  --md <path>     Write the Markdown report to <path>.");
        writer.WriteLine("  --json <path>   Write the JSON report to <path>.");
        writer.WriteLine("  --quiet         Do not print the Markdown report to stdout.");
        writer.WriteLine("  --strict        Exit non-zero on CONDITIONAL/INCONCLUSIVE as well as KILL.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 = GO/CONDITIONAL/INCONCLUSIVE (unless --strict), 2 = KILL, 3 = strict non-GO, 64 = usage.");
    }
}
