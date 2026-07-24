// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class MigrateValidateCommand : Command, IShortDescription
{
    public string ShortDescription => "Validate a migrated WinUI 3 project (residue / single-project / manifest gate)";

    public static Argument<DirectoryInfo> DirectoryArgument { get; }
    public static Option<bool> FromUwpOption { get; }
    public static Option<FileInfo?> ProjectOption { get; }

    static MigrateValidateCommand()
    {
        DirectoryArgument = new Argument<DirectoryInfo>("directory")
        {
            Description = "Migrated WinUI 3 project root to validate (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne
        };
        DirectoryArgument.AcceptExistingOnly();

        FromUwpOption = new Option<bool>("--from-uwp")
        {
            Description = "Validate a UWP→WinUI 3 migration (currently the only supported direction)."
        };

        ProjectOption = new Option<FileInfo?>("--project")
        {
            Description = "Target a specific .csproj (default: scan the whole directory)."
        };
    }

    public MigrateValidateCommand()
        : base("validate", "Validate a migrated WinUI 3 project before declaring the migration done. Runs source-only static gates: UWP API/namespace residue (backed by the analyzer), single-project layout, MainWindow shell wiring, and Package.appxmanifest packaging requirements. Emits sanitized [PASS]/[FAIL]/[WARN] lines to stdout with full diagnostics in .validator-diagnostics.txt, and returns non-zero when any [FAIL] remains. Build/run health is covered separately by 'winapp build' / 'winapp run'.")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(FromUwpOption);
        Options.Add(ProjectOption);
    }

    // ── Residue text markers (UWP-only namespaces / csproj shape) ───────────────
    private static readonly (Regex Rx, string Name)[] ResidueMarkers =
    [
        (UsingWindowsUiXaml(), "using Windows.UI.Xaml"),
        (XmlnsUsingWindowsUiXaml(), "xmlns using:Windows.UI.Xaml"),
        (UwpNugetPackage(), "UWP PackageReference (Microsoft.NETCore.UniversalWindowsPlatform)"),
        (UapTargetPlatform(), "<TargetPlatformIdentifier>UAP"),
        (AppContainerExe(), "<OutputType>AppContainerExe"),
    ];

    private static readonly string[] ExcludeDirs = ["bin", "obj", ".uwp-source", ".vs", ".git", ".github", ".copilot", "Generated Files", "node_modules"];

    public class Handler(ICurrentDirectoryProvider currentDirectoryProvider, IMigrateAnalyzerDriver driver) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var target = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var project = parseResult.GetValue(ProjectOption);
            var root = target.FullName;

            var diagnostics = new StringBuilder();
            int failures = 0;

            var deferred = LoadDeferredFiles(root);

            // ─── 1. Residue: analyzer-backed API residue (startup-crash / unsupported) ───
            failures += await CheckAnalyzerResidueAsync(target, project, deferred, diagnostics, cancellationToken);

            // ─── 1b. Residue: text markers (UWP namespaces / csproj shape) ───
            failures += CheckTextResidue(root, deferred, diagnostics);

            // ─── 2. Shell wiring integrity ───
            failures += CheckShellWiring(root, diagnostics);

            // ─── 3. Single-project layout ───
            failures += CheckSingleProject(root, diagnostics);

            // ─── 4. Package.appxmanifest packaging requirements ───
            failures += CheckManifest(root, diagnostics);

            // ─── Write diagnostics + summary ───
            var diagPath = Path.Combine(root, ".validator-diagnostics.txt");
            if (diagnostics.Length > 0)
            {
                try { await File.WriteAllTextAsync(diagPath, diagnostics.ToString(), cancellationToken); } catch { /* best effort */ }
            }

            if (failures == 0)
            {
                Console.Out.WriteLine("[PASS] Validation gate — all checks passed. Run 'winapp build' to confirm a clean compile.");
                return 0;
            }

            Console.Out.WriteLine($"[FAIL] Validation gate — {failures} check(s) failed. Full diagnostics in .validator-diagnostics.txt.");
            return 1;
        }

        private async Task<int> CheckAnalyzerResidueAsync(DirectoryInfo target, FileInfo? project, HashSet<string> deferred, StringBuilder diag, CancellationToken ct)
        {
            MigrateAnalyzerRun run;
            try
            {
                run = await driver.RunAsync(target, project, fromUwp: true, ct);
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine($"[WARN] Residue (API) — analyzer driver failed to run: {ex.Message}. Skipped API-residue check.");
                return 0;
            }

            if (!run.DriverFound)
            {
                Console.Out.WriteLine("[WARN] Residue (API) — analyzer driver 'winui-analyze' not found; skipped API-residue check. Text-marker residue still enforced.");
                return 0;
            }

            MigrateAnalyzeReport? report = null;
            try
            {
                report = JsonSerializer.Deserialize(run.StdOut, MigrateJsonContext.Default.MigrateAnalyzeReport);
            }
            catch (JsonException)
            {
                // fall through to null handling below
            }

            if (report is null)
            {
                Console.Out.WriteLine("[WARN] Residue (API) — analyzer produced no parseable output; skipped API-residue check.");
                if (run.StdErr.Length > 0) { diag.AppendLine("[Residue API driver stderr]").AppendLine(run.StdErr.TrimEnd()).AppendLine(); }
                return 0;
            }

            // A migrated (non-deferred) file must carry no must-fix API residue.
            var hits = new List<(string File, int Line, string Id, string Severity, string Detected)>();
            foreach (var file in report.Files)
            {
                var rel = Normalize(file.Path);
                if (deferred.Contains(rel)) { continue; }
                foreach (var f in file.Findings)
                {
                    var sev = f.Severity ?? "";
                    if (sev is "startup-crash" or "unsupported")
                    {
                        hits.Add((rel, f.Location?.Line ?? 0, f.Id ?? "?", sev, f.Detected ?? ""));
                    }
                }
            }

            if (hits.Count == 0)
            {
                Console.Out.WriteLine("[PASS] Residue (API) — 0 startup-crash / unsupported API references in non-deferred files.");
                return 0;
            }

            Console.Out.WriteLine($"[FAIL] Residue (API) — {hits.Count} must-fix UWP API reference(s) remain in non-deferred files (see .validator-diagnostics.txt):");
            diag.AppendLine("[Residue — API (analyzer)]");
            foreach (var g in hits.GroupBy(h => h.File).Take(30))
            {
                foreach (var h in g.Take(10)) { Console.Out.WriteLine($"       {g.Key}:{h.Line}"); }
                if (g.Count() > 10) { Console.Out.WriteLine($"       {g.Key}: ({g.Count() - 10} more)"); }
                foreach (var h in g) { diag.AppendLine($"  {h.File}:{h.Line}  [{h.Id} {h.Severity}]  {h.Detected}"); }
            }
            diag.AppendLine();
            return 1;
        }

        private static int CheckTextResidue(string root, HashSet<string> deferred, StringBuilder diag)
        {
            var hits = new List<(string File, int Line, string Name, string Snippet)>();
            foreach (var file in EnumerateSource(root, ".cs", ".xaml", ".csproj"))
            {
                var rel = Normalize(Path.GetRelativePath(root, file));
                if (deferred.Contains(rel)) { continue; }
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }
                foreach (var (rx, name) in ResidueMarkers)
                {
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (rx.IsMatch(lines[i]))
                        {
                            hits.Add((rel, i + 1, name, lines[i].Trim()));
                            break; // one hit per marker per file is enough
                        }
                    }
                }
            }

            if (hits.Count == 0)
            {
                Console.Out.WriteLine("[PASS] Residue (markers) — 0 UWP namespace/csproj markers in non-deferred .cs/.xaml/.csproj.");
                return 0;
            }

            Console.Out.WriteLine($"[FAIL] Residue (markers) — {hits.Count} UWP-only marker(s) remain in non-deferred files (see .validator-diagnostics.txt):");
            diag.AppendLine("[Residue — markers (text)]");
            foreach (var g in hits.GroupBy(h => h.File).Take(30))
            {
                foreach (var h in g.Take(10)) { Console.Out.WriteLine($"       {g.Key}:{h.Line}"); }
                foreach (var h in g) { diag.AppendLine($"  {h.File}:{h.Line}  {h.Name}  | {h.Snippet}"); }
            }
            diag.AppendLine();
            return 1;
        }

        private static int CheckShellWiring(string root, StringBuilder diag)
        {
            var fails = new List<string>();
            var mainWindowXaml = FindFirst(root, "MainWindow.xaml");
            var mainWindowCs = FindFirst(root, "MainWindow.xaml.cs");

            if (mainWindowXaml is not null)
            {
                var xaml = SafeRead(mainWindowXaml);
                if (!RootFrame().IsMatch(xaml))
                {
                    fails.Add("MainWindow.xaml is missing <Frame x:Name=\"RootFrame\"> — app content will not render.");
                }
            }
            if (mainWindowCs is not null)
            {
                var cs = SafeRead(mainWindowCs);
                if (DestructiveContent().IsMatch(cs))
                {
                    fails.Add("MainWindow.xaml.cs assigns Content directly — this overwrites XAML-defined layout and causes a blank window.");
                }
            }

            if (fails.Count == 0)
            {
                Console.Out.WriteLine("[PASS] Shell wiring — MainWindow Frame intact, no destructive Content override.");
                return 0;
            }
            foreach (var f in fails) { Console.Out.WriteLine($"[FAIL] Shell wiring — {f}"); }
            diag.AppendLine("[Shell wiring]");
            foreach (var f in fails) { diag.AppendLine("  " + f); }
            diag.AppendLine();
            return 1;
        }

        private static int CheckSingleProject(string root, StringBuilder diag)
        {
            var projects = EnumerateSource(root, ".csproj").ToList();
            if (projects.Count <= 1)
            {
                Console.Out.WriteLine("[PASS] Project layout — single project, no nested duplicate .csproj.");
                return 0;
            }

            var primary = projects.OrderBy(p => p.Count(c => c is '\\' or '/')).First();
            var nested = projects.Where(p => !string.Equals(p, primary, StringComparison.OrdinalIgnoreCase)).ToList();
            Console.Out.WriteLine($"[FAIL] Nested duplicate project — {nested.Count} extra .csproj inside the project tree (poisons the outer build via CS0579 duplicate-attribute errors):");
            diag.AppendLine("[Nested duplicate project]");
            foreach (var n in nested)
            {
                var rel = Normalize(Path.GetRelativePath(root, n));
                Console.Out.WriteLine($"       {rel}");
                diag.AppendLine("  " + n);
            }
            Console.Out.WriteLine("       Fix: delete the nested project folder (e.g. a stray 'AppX\\' source copy and its bin/obj). Keep exactly one .csproj.");
            diag.AppendLine();
            return 1;
        }

        private static int CheckManifest(string root, StringBuilder diag)
        {
            var manifest = Path.Combine(root, "Package.appxmanifest");
            if (!File.Exists(manifest))
            {
                Console.Out.WriteLine("[WARN] Package.appxmanifest not found at project root — skipped manifest checks.");
                return 0;
            }

            var text = SafeRead(manifest);
            int fails = 0;

            // 5a. image references resolvable under the project
            var imageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in LogoElement().Matches(text)) { imageRefs.Add(m.Groups[1].Value.Trim()); }
            foreach (Match m in ImageAttr().Matches(text)) { imageRefs.Add(m.Groups[1].Value.Trim()); }
            var missing = new List<string>();
            foreach (var reff in imageRefs)
            {
                var abs = Path.Combine(root, reff.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs)) { continue; }
                var dir = Path.GetDirectoryName(abs);
                var baseName = Path.GetFileNameWithoutExtension(abs);
                var ext = Path.GetExtension(abs);
                if (dir is not null && Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir, $"{baseName}*{ext}").Any())
                {
                    continue; // scale-*/targetsize-* variant present
                }
                missing.Add(reff);
            }
            if (missing.Count == 0 && imageRefs.Count > 0)
            {
                Console.Out.WriteLine($"[PASS] Manifest images — all {imageRefs.Count} image reference(s) resolvable.");
            }
            else if (missing.Count > 0)
            {
                Console.Out.WriteLine($"[FAIL] Manifest images — {missing.Count} referenced image file(s) do not exist on disk (see .validator-diagnostics.txt).");
                diag.AppendLine("[Manifest images missing]");
                foreach (var r in missing) { diag.AppendLine("  " + r); }
                diag.AppendLine();
                fails++;
            }

            // 5b. WinUI 3 packaging requirements
            var pkgFails = new List<string>();
            if (!TargetDeviceDesktop().IsMatch(text))
            {
                pkgFails.Add("<TargetDeviceFamily> is not Windows.Desktop (Windows.Universal is UWP-only; the registrar rejects it for a Win32 entrypoint).");
            }
            var hasRescapNs = RescapNamespace().IsMatch(text);
            var rescapIgnorable = RescapIgnorable().IsMatch(text);
            if (!hasRescapNs || !rescapIgnorable)
            {
                pkgFails.Add("missing the rescap namespace declaration (add xmlns:rescap=\"…/restrictedcapabilities\" on <Package> and append 'rescap' to IgnorableNamespaces).");
            }
            if (!RunFullTrust().IsMatch(text))
            {
                pkgFails.Add("missing <rescap:Capability Name=\"runFullTrust\" /> — 'winapp run' fails registration without it.");
            }
            if (pkgFails.Count == 0)
            {
                Console.Out.WriteLine("[PASS] Manifest packaging — Windows.Desktop target + rescap:runFullTrust capability declared.");
            }
            else
            {
                foreach (var f in pkgFails) { Console.Out.WriteLine($"[FAIL] Manifest packaging — {f}"); }
                diag.AppendLine("[Manifest packaging]");
                foreach (var f in pkgFails) { diag.AppendLine("  " + f); }
                diag.AppendLine();
                fails += pkgFails.Count;
            }

            return fails;
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static HashSet<string> LoadDeferredFiles(string root)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deferredMd = Path.Combine(root, "MIGRATION-DEFERRED.md");
            if (!File.Exists(deferredMd)) { return set; }
            foreach (var line in File.ReadLines(deferredMd))
            {
                var m = DeferredRow().Match(line);
                if (!m.Success) { continue; }
                var file = m.Groups[1].Value.Trim();
                if (file.Length == 0 || file.Equals("File", StringComparison.OrdinalIgnoreCase) || file.StartsWith('-') || file == "(none)")
                {
                    continue;
                }
                set.Add(Normalize(file));
            }
            return set;
        }

        private static IEnumerable<string> EnumerateSource(string root, params string[] extensions)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(root, f);
                if (rel.Split('\\', '/').Any(seg => ExcludeDirs.Contains(seg, StringComparer.OrdinalIgnoreCase))) { continue; }
                if (extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))) { yield return f; }
            }
        }

        private static string? FindFirst(string root, string fileName)
            => EnumerateSource(root, Path.GetExtension(fileName))
                .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return string.Empty; }
        }

        private static string Normalize(string relPath) => relPath.Replace('\\', '/');
    }

    // ── Compiled regexes ─────────────────────────────────────────────────────
    [GeneratedRegex(@"using\s+Windows\.UI\.Xaml")] private static partial Regex UsingWindowsUiXaml();
    [GeneratedRegex("xmlns:[A-Za-z0-9]+=\"using:Windows\\.UI\\.Xaml")] private static partial Regex XmlnsUsingWindowsUiXaml();
    [GeneratedRegex(@"Microsoft\.NETCore\.UniversalWindowsPlatform")] private static partial Regex UwpNugetPackage();
    [GeneratedRegex(@"<TargetPlatformIdentifier>\s*UAP")] private static partial Regex UapTargetPlatform();
    [GeneratedRegex(@"<OutputType>\s*AppContainerExe")] private static partial Regex AppContainerExe();
    [GeneratedRegex("<Frame\\b[^>]*x:Name\\s*=\\s*\"RootFrame\"")] private static partial Regex RootFrame();
    [GeneratedRegex(@"MainWindow\s*\.\s*Content\s*=|(?<![A-Za-z0-9_.])Content\s*=\s*new\s+(Frame|Page|MainPage|Grid)\b")] private static partial Regex DestructiveContent();
    [GeneratedRegex("<Logo>([^<]+)</Logo>")] private static partial Regex LogoElement();
    [GeneratedRegex("(?:Square150x150Logo|Square71x71Logo|Square44x44Logo|Square310x310Logo|Wide310x150Logo|Image|BackgroundImage)\\s*=\\s*\"([^\"]+\\.(?:png|jpg|jpeg|ico|svg|gif))\"", RegexOptions.IgnoreCase)] private static partial Regex ImageAttr();
    [GeneratedRegex("<TargetDeviceFamily\\s+Name=\"Windows\\.Desktop\"")] private static partial Regex TargetDeviceDesktop();
    [GeneratedRegex("xmlns:rescap\\s*=\\s*\"http://schemas\\.microsoft\\.com/appx/manifest/foundation/windows10/restrictedcapabilities\"")] private static partial Regex RescapNamespace();
    [GeneratedRegex("IgnorableNamespaces\\s*=\\s*\"[^\"]*\\brescap\\b[^\"]*\"")] private static partial Regex RescapIgnorable();
    [GeneratedRegex("<rescap:Capability\\s+Name=\"runFullTrust\"\\s*/>")] private static partial Regex RunFullTrust();
    [GeneratedRegex(@"^\|\s*([^|]+?)\s*\|")] private static partial Regex DeferredRow();
}
