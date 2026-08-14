// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.WindowsAppSDK.Analyzers.Rules;

namespace Microsoft.WindowsAppSDK.Analyzers.Driver;

/// <summary>
/// Self-contained driver that hosts the winui-analyzer over still-UWP source (no restore /
/// build) and emits the migration-plan JSON contract (v1.0) to stdout. Provides the
/// out-of-build entry point the UWP -> WinUI 3 migration tooling consumes at Step 0.
/// </summary>
internal static class Program
{
    private const string SchemaVersion = "1.0";
    private const string MigrationTierKey = "MigrationTier";
    private const string StartupCrashTier = "startup-crash";
    private const string SensitiveTier = "sensitive";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<int> Main(string[] args)
    {
        string? root = null;
        string? projectFile = null;
        bool fromUwp = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--root" && i + 1 < args.Length) { root = args[++i]; }
            else if (a is "--project" && i + 1 < args.Length) { projectFile = args[++i]; }
            else if (a is "--from-uwp") { fromUwp = true; }
            else if (!a.StartsWith("--", StringComparison.Ordinal) && root is null) { root = a; }
        }

        if (root is null)
        {
            await Console.Error.WriteLineAsync(
                "usage: winui-analyze [--root] <uwp-project-dir> [--project <file.csproj>] [--from-uwp]");
            return 2;
        }

        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            await Console.Error.WriteLineAsync($"error: directory not found: {root}");
            return 2;
        }

        try
        {
            var report = await AnalyzeAsync(root, projectFile, fromUwp);
            Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonOpts));
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"error: analyze failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<Report> AnalyzeAsync(string root, string? projectFile, bool fromUwp)
    {
        var csFiles = EnumerateSource(root, "*.cs");
        var trees = csFiles
            .Select(p => CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(p), Encoding.UTF8), path: p))
            .ToImmutableArray();

        var additionalTexts = EnumerateSource(root, "*.xaml")
            .Concat(EnumerateSource(root, "*.appxmanifest"))
            .Select(p => (AdditionalText)new PhysicalAdditionalText(p))
            .ToImmutableArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "MigrationAnalysisTarget",
            syntaxTrees: trees,
            references: TrustedReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzerOptions = fromUwp
            ? new AnalyzerOptions(additionalTexts, new ForceMigrationOptionsProvider())
            : new AnalyzerOptions(additionalTexts);
        var withAnalyzers = compilation.WithAnalyzers(Analyzers, analyzerOptions);

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        // Group by originating file (relative to root); keep a stable order.
        var byFile = new SortedDictionary<string, List<Finding>>(StringComparer.Ordinal);
        var featureAreaByFile = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var d in diagnostics.Where(IsOurs))
        {
            var span = d.Location.GetLineSpan();
            var absPath = span.Path;
            if (string.IsNullOrEmpty(absPath)) continue;
            var rel = Relativize(root, absPath);

            var severity = SeverityOf(d);
            if (!byFile.TryGetValue(rel, out var list))
            {
                list = new List<Finding>();
                byFile[rel] = list;
            }

            list.Add(new Finding(
                Id: d.Id,
                Severity: severity,
                Detected: DetectedFrom(d),
                Location: new FindingLocation(rel, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1),
                Fix: FixOf(d, severity)));

            if (d.Id == "WUI1010" && !featureAreaByFile.ContainsKey(rel))
            {
                var area = FeatureAreaFrom(d);
                if (area is not null) featureAreaByFile[rel] = area;
            }
        }

        var files = byFile.Select(kvp =>
        {
            var sevs = kvp.Value.Select(f => f.Severity).ToList();
            featureAreaByFile.TryGetValue(kvp.Key, out var area);
            return new FileEntry(
                Path: kvp.Key,
                Disposition: DispositionOf(sevs),
                FeatureArea: area,
                Findings: kvp.Value
                    .OrderBy(f => f.Location.Line)
                    .ThenBy(f => f.Location.Column)
                    .ToList());
        }).ToList();

        var totalFindings = files.Sum(f => f.Findings.Count);
        var crashFindings = files.Sum(f => f.Findings.Count(x => x.Severity == StartupCrashTier));

        return new Report(
            SchemaVersion,
            new Source(root.Replace('\\', '/'), projectFile),
            new Summary(files.Count, totalFindings, crashFindings),
            files);
    }

    // ── Analyzer set ────────────────────────────────────────────────────────
    private static ImmutableArray<DiagnosticAnalyzer> Analyzers =>
        ImmutableArray.Create<DiagnosticAnalyzer>(
            new UwpApiAnalyzer(),
            new ApiMappingAnalyzer(),
            new XamlAnalyzer(),
            new XamlCodeBehindAnalyzer(),
            new TabViewContentAnalyzer(),
            new AttachedPropertyAnalyzer(),
            new MvvmPatternAnalyzer(),
            new WebView2InitAnalyzer(),
            new GenAiApiAnalyzer());

    private static readonly ImmutableHashSet<string> OurIds =
        Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).ToImmutableHashSet();

    private static bool IsOurs(Diagnostic d) => OurIds.Contains(d.Id);

    // ── Mapping: analyzer diagnostic → contract fields ──────────────────────
    private static string SeverityOf(Diagnostic d)
    {
        // Explicit migration-tier signal (startup-crash / sensitive) always wins.
        if (d.Properties.TryGetValue(MigrationTierKey, out var tier)
            && (tier == StartupCrashTier || tier == SensitiveTier))
        {
            return tier!;
        }
        return d.Id switch
        {
            "WUI1002" => "unsupported",
            "WUI1001" => "adaptable",
            // WUI1010 feature hints are informational unless flagged sensitive above.
            "WUI1010" => "adaptable",
            _ => "adaptable",
        };
    }

    private static string DispositionOf(IReadOnlyCollection<string> severities)
    {
        if (severities.Contains("unsupported")) return "defer";
        if (severities.Contains("sensitive")) return "sequential-manual";
        return "migrate";
    }

    private static string DetectedFrom(Diagnostic d)
    {
        var msg = d.GetMessage(CultureInfo.InvariantCulture);
        return d.Id switch
        {
            "WUI1001" => Before(msg, " \u2192 "),
            "WUI1002" => Before(msg, " is not supported"),
            "WUI1010" => Before(msg, " ("),
            _ => d.Descriptor.Title.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string? FeatureAreaFrom(Diagnostic d)
    {
        // WUI1010 message shape: "<prefix> (<area>): <note>"
        var msg = d.GetMessage(CultureInfo.InvariantCulture);
        var open = msg.IndexOf(" (", StringComparison.Ordinal);
        var close = msg.IndexOf("):", StringComparison.Ordinal);
        if (open >= 0 && close > open + 2)
        {
            return msg.Substring(open + 2, close - (open + 2));
        }
        return null;
    }

    private static Fix? FixOf(Diagnostic d, string severity)
    {
        if (severity == "unsupported") return null;
        var refUri = string.IsNullOrEmpty(d.Descriptor.HelpLinkUri) ? null : d.Descriptor.HelpLinkUri;
        return new Fix(refUri, d.GetMessage(CultureInfo.InvariantCulture));
    }

    private static string Before(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }

    // ── File / reference helpers ────────────────────────────────────────────
    private static IEnumerable<string> EnumerateSource(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string Relativize(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Replace('\\', '/');
    }

    private static ImmutableArray<MetadataReference> TrustedReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        var refs = trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // Best-effort: add the UWP union metadata so semantic (member/type) rules — including the
        // DisplayRequest startup-crash tier — can resolve Windows.* symbols. Absence degrades
        // gracefully to syntactic-only findings.
        var winmd = LocateWindowsWinmd();
        if (winmd != null)
        {
            refs.Add(MetadataReference.CreateFromFile(winmd));
        }

        return refs.ToImmutableArray();
    }

    private static string? LocateWindowsWinmd()
    {
        foreach (var pf in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var unionRoot = Path.Combine(pf, "Windows Kits", "10", "UnionMetadata");
            if (!Directory.Exists(unionRoot)) continue;

            var newest = Directory.EnumerateDirectories(unionRoot)
                .Select(d => (dir: d, name: Path.GetFileName(d)))
                .Where(x => Version.TryParse(x.name, out _))
                .OrderByDescending(x => Version.Parse(x.name))
                .Select(x => Path.Combine(x.dir, "Windows.winmd"))
                .FirstOrDefault(File.Exists);
            if (newest != null) return newest;
        }
        return null;
    }

    private sealed class PhysicalAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public PhysicalAdditionalText(string path)
        {
            Path = path;
            _text = SourceText.From(File.ReadAllText(path), Encoding.UTF8);
        }
        public override string Path { get; }
        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    // B4: forces migration-only rules to fire regardless of source markers. Set when the caller
    // passes --from-uwp; read by ProjectContext.Detect via the global analyzer-config options.
    private sealed class ForceMigrationOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ForcedOptions();
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class ForcedOptions : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (string.Equals(key, "build_property.WinUIMigrationFromUwp", StringComparison.Ordinal))
                {
                    value = "true";
                    return true;
                }
                value = null!;
                return false;
            }
        }
    }
}

// ── Contract DTOs (migration-plan v1.0) ─────────────────────────────────────
internal sealed record Report(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("source")] Source Source,
    [property: JsonPropertyName("summary")] Summary Summary,
    [property: JsonPropertyName("files")] IReadOnlyList<FileEntry> Files);

internal sealed record Source(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("projectFile")] string? ProjectFile);

internal sealed record Summary(
    [property: JsonPropertyName("filesAnalyzed")] int FilesAnalyzed,
    [property: JsonPropertyName("findings")] int Findings,
    [property: JsonPropertyName("startupCrashFindings")] int StartupCrashFindings);

internal sealed record FileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("featureArea")] string? FeatureArea,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings);

internal sealed record Finding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("detected")] string Detected,
    [property: JsonPropertyName("location")] FindingLocation Location,
    [property: JsonPropertyName("fix")] Fix? Fix);

internal sealed record FindingLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column);

internal sealed record Fix(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("summary")] string Summary);
