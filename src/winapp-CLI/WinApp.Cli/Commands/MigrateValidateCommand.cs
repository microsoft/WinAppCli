// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class MigrateValidateCommand : Command, IShortDescription
{
    public string ShortDescription => "Validate a migrated WinUI 3 project (residue / single-project / manifest gate)";

    public static Argument<DirectoryInfo> DirectoryArgument { get; }
    public static Option<bool> FromUwpOption { get; }

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
    }

    public MigrateValidateCommand()
        : base("validate", "Validate a migrated WinUI 3 project before declaring the migration done. Runs source-only static gates: UWP namespace/csproj residue markers, single-project layout, MainWindow shell wiring, and Package.appxmanifest packaging requirements. Emits sanitized [PASS]/[FAIL]/[WARN] lines to stdout with full diagnostics in .validator-diagnostics.txt, and returns non-zero when any [FAIL] remains. Build/run health is covered separately by 'winapp build' / 'winapp run'.")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(FromUwpOption);
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

    public class Handler(ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var originalOut = Console.Out;
            if (quiet) { Console.SetOut(new QuietFilteringTextWriter(originalOut)); }
            try
            {
            var target = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var root = target.FullName;

            var diagnostics = new StringBuilder();
            int failures = 0;

            var deferred = LoadDeferredFiles(root);

            // ─── 1. Residue: text markers (UWP namespaces / csproj shape) ───
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
            else
            {
                // Clean run: remove any stale diagnostics from a previous failed run so a [PASS]
                // is not contradicted by a leftover file describing old failures.
                try { if (File.Exists(diagPath)) { File.Delete(diagPath); } } catch { /* best effort */ }
            }

            if (failures == 0)
            {
                Console.Out.WriteLine("[PASS] Validation gate — all checks passed. Run 'winapp build' to confirm a clean compile.");
                return 0;
            }

            Console.Out.WriteLine($"[FAIL] Validation gate — {failures} check(s) failed. Full diagnostics in .validator-diagnostics.txt.");
            return 1;
            }
            finally
            {
                if (quiet) { Console.SetOut(originalOut); }
            }
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

                // Match markers against a copy with comments (and, for C#, string/char literals)
                // blanked out, so prose like `// Previously used using Windows.UI.Xaml` or a string
                // literal "using Windows.UI.Xaml" does not fail the gate. The original lines are
                // still used for the diagnostic snippet.
                var isCSharp = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                var scan = SanitizeForMarkers(lines, isCSharp);
                foreach (var (rx, name) in ResidueMarkers)
                {
                    for (int i = 0; i < scan.Length; i++)
                    {
                        if (rx.IsMatch(scan[i]))
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

            if (mainWindowXaml is null)
            {
                fails.Add("MainWindow.xaml not found — the WinUI 3 app shell is missing, so app content cannot render.");
            }
            else
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
            if (projects.Count == 0)
            {
                Console.Out.WriteLine("[FAIL] Project layout — no .csproj found; a migrated WinUI 3 project must contain exactly one project file.");
                diag.AppendLine("[Project layout]").AppendLine("  No .csproj found under the project root.").AppendLine();
                return 1;
            }
            if (projects.Count == 1)
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

        // Returns a copy of the source lines with comments (and, for C#, string/char literals)
        // blanked to spaces, so residue markers only match real code/markup — not the same text
        // appearing in a comment or a string literal. This is a lightweight tokenizer, not a full
        // parser, but it removes the common false positives. Block comments (C# /* */ and XML
        // <!-- -->) are tracked across lines; single-line C# strings are not carried over.
        private static string[] SanitizeForMarkers(string[] lines, bool isCSharp)
        {
            var result = new string[lines.Length];
            var inBlockComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                result[i] = isCSharp
                    ? SanitizeCSharpLine(lines[i], ref inBlockComment)
                    : SanitizeXmlLine(lines[i], ref inBlockComment);
            }
            return result;
        }

        private static string SanitizeCSharpLine(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            bool inLineComment = false, inString = false, inChar = false, inVerbatim = false;
            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];
                char next = j + 1 < line.Length ? line[j + 1] : '\0';
                if (inLineComment) { sb.Append(' '); continue; }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; sb.Append("  "); j++; }
                    else { sb.Append(' '); }
                    continue;
                }
                if (inString)
                {
                    if (inVerbatim && c == '"' && next == '"') { sb.Append("  "); j++; continue; }
                    if (!inVerbatim && c == '\\' && next != '\0') { sb.Append("  "); j++; continue; }
                    if (c == '"') { inString = false; inVerbatim = false; }
                    sb.Append(' ');
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\' && next != '\0') { sb.Append("  "); j++; continue; }
                    if (c == '\'') { inChar = false; }
                    sb.Append(' ');
                    continue;
                }
                if (c == '/' && next == '/') { inLineComment = true; sb.Append(' '); continue; }
                if (c == '/' && next == '*') { inBlockComment = true; sb.Append(' '); continue; }
                if (c == '@' && next == '"') { inString = true; inVerbatim = true; sb.Append("  "); j++; continue; }
                if (c == '"') { inString = true; inVerbatim = false; sb.Append(' '); continue; }
                if (c == '\'') { inChar = true; sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string SanitizeXmlLine(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            for (int j = 0; j < line.Length; j++)
            {
                if (inBlockComment)
                {
                    if (j + 2 < line.Length && line[j] == '-' && line[j + 1] == '-' && line[j + 2] == '>')
                    { inBlockComment = false; sb.Append("   "); j += 2; }
                    else { sb.Append(' '); }
                    continue;
                }
                if (j + 3 < line.Length && line[j] == '<' && line[j + 1] == '!' && line[j + 2] == '-' && line[j + 3] == '-')
                { inBlockComment = true; sb.Append("    "); j += 3; continue; }
                sb.Append(line[j]);
            }
            return sb.ToString();
        }
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
