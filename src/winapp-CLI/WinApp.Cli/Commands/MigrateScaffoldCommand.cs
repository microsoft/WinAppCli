// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.RegularExpressions;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp migrate scaffold --from-uwp</c> — the generation/transform half of a UWP → WinUI 3
/// migration. Copies UWP source into an existing WinUI 3 scaffold, merges SDK-sample shared assets,
/// rewrites the <c>Windows.UI.Xaml</c> namespace, patches the csproj RuntimeIdentifier for cross-arch
/// F5, neutralizes content-filter-prone helper classes, and wires up the MainWindow RootFrame + initial
/// navigation. It does NOT triage APIs or inject TODO markers — that is <c>migrate analyze</c>'s job.
/// </summary>
internal partial class MigrateScaffoldCommand : Command, IShortDescription
{
    public string ShortDescription => "Copy UWP source into a WinUI 3 scaffold and apply mechanical transforms";

    public static Argument<DirectoryInfo> SourceArgument { get; }
    public static Option<DirectoryInfo> TargetOption { get; }
    public static Option<bool> FromUwpOption { get; }

    // Source files worth copying (source + assets). Build artifacts and projects are excluded.
    private static readonly string[] CopyExtensions =
    [
        ".xaml", ".cs", ".resw", ".resjson",
        ".png", ".jpg", ".jpeg", ".svg", ".ico", ".gif"
    ];

    private static readonly string[] ExcludeDirSegments =
    [
        "bin", "obj", ".uwp-source", ".vs", ".git", ".github", ".copilot"
    ];

    private const string RidFixMarker = "<!-- arm64-f5-fix:winapp-migrate-scaffold -->";
    private const string FrameMarker = "<!-- shell-frame:winapp-migrate-scaffold -->";
    private const string NavMarker = "// shell-nav:winapp-migrate-scaffold";

    static MigrateScaffoldCommand()
    {
        SourceArgument = new Argument<DirectoryInfo>("source")
        {
            Description = "UWP project source folder (contains the .csproj and Package.appxmanifest)."
        };
        SourceArgument.AcceptExistingOnly();

        TargetOption = new Option<DirectoryInfo>("--target")
        {
            Description = "Existing WinUI 3 scaffold to migrate into (produced by 'dotnet new winui').",
            Required = true
        };
        TargetOption.AcceptExistingOnly();

        FromUwpOption = new Option<bool>("--from-uwp")
        {
            Description = "Migrate from UWP source (currently the only supported source)."
        };
    }

    public MigrateScaffoldCommand()
        : base("scaffold", "Copy UWP source (C#/XAML/assets) into an existing WinUI 3 scaffold and apply the mechanical, deterministic transforms a migration always needs: merge SDK-sample shared/ + SharedContent/ assets, preserve the original .csproj/.appxmanifest under .uwp-source/, patch the csproj RuntimeIdentifier for x86/x64/ARM64 F5, rewrite Windows.UI.Xaml -> Microsoft.UI.Xaml, neutralize content-filter-prone helper classes, and wire the MainWindow RootFrame + initial Navigate. Triage / per-line findings are produced separately by 'migrate analyze'.")
    {
        Arguments.Add(SourceArgument);
        Options.Add(TargetOption);
        Options.Add(FromUwpOption);
    }

    public class Handler(ILogger<MigrateScaffoldCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var source = parseResult.GetValue(SourceArgument)!;
            var target = parseResult.GetValue(TargetOption)!;

            var sourceRoot = source.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetRoot = target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Console.Out.WriteLine("==> winapp migrate scaffold --from-uwp");
            Console.Out.WriteLine($"    Source : {sourceRoot}");
            Console.Out.WriteLine($"    Target : {targetRoot}");

            var copied = new List<string>();

            // ── 1. Copy source files (everything but the project) ────────────────
            foreach (var file in EnumerateFiles(sourceRoot))
            {
                if (!HasCopyExtension(file))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(sourceRoot, file);
                CopyInto(file, Path.Combine(targetRoot, rel));
                copied.Add(rel);
            }
            Console.Out.WriteLine($"    Copied {copied.Count} source files");

            // ── 1b. Merge sibling shared/ (cross-language SDK-sample layout) ─────
            MergeSiblingShared(sourceRoot, targetRoot, copied);

            // ── 1c. Merge top-level SharedContent/ (repo-wide sample assets) ─────
            MergeSharedContent(sourceRoot, targetRoot, copied);

            // ── 1d. Register Styles.xaml in App.xaml MergedDictionaries ──────────
            RegisterStyles(targetRoot);

            // ── 2. Preserve UWP .csproj / .appxmanifest under .uwp-source/ ───────
            PreserveUwpReferences(sourceRoot, targetRoot);

            // ── 2b. Patch WinUI 3 csproj RuntimeIdentifier for cross-arch F5 ─────
            PatchRuntimeIdentifier(targetRoot);

            // ── 3. Namespace rewrite: Windows.UI.Xaml -> Microsoft.UI.Xaml ──────
            RewriteNamespaces(targetRoot);

            // ── 4. Neutralize content-filter-prone helper classes ───────────────
            NeutralizeFilterProneClasses(targetRoot, copied);

            // ── 5. Wire MainWindow RootFrame + initial navigation ───────────────
            WireRootFrame(targetRoot);

            Console.Out.WriteLine();
            Console.Out.WriteLine("=== SCAFFOLD COMPLETE ===");
            Console.Out.WriteLine("Next:");
            Console.Out.WriteLine("  1. Run 'winapp migrate analyze <target> --from-uwp' for the triage plan (JSON).");
            Console.Out.WriteLine("  2. Resolve the findings, building per-file (never per-finding).");
            Console.Out.WriteLine("  3. Run 'winapp migrate validate <target> --from-uwp' before declaring done.");
            Console.Out.WriteLine("=========================");

            return Task.FromResult(0);
        }

        // ───────────────────────────── 1b ──────────────────────────────────────
        private void MergeSiblingShared(string sourceRoot, string targetRoot, List<string> copied)
        {
            var sharedDir = Path.Combine(Path.GetDirectoryName(sourceRoot) ?? sourceRoot, "shared");
            if (!Directory.Exists(sharedDir))
            {
                return;
            }

            var sharedRoot = new DirectoryInfo(sharedDir).FullName;
            int merged = 0;
            var collisions = new List<string>();
            foreach (var file in EnumerateFiles(sharedRoot))
            {
                if (!HasCopyExtension(file))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(sharedRoot, file);
                var dst = Path.Combine(targetRoot, rel);
                if (File.Exists(dst)) { collisions.Add(rel); continue; }
                CopyInto(file, dst);
                copied.Add(rel);
                merged++;
            }
            if (merged > 0)
            {
                Console.Out.WriteLine($"    Merged {merged} file(s) from sibling shared/ ({sharedRoot})");
            }

            if (collisions.Count > 0)
            {
                logger.LogWarning("Skipped {Count} shared/ file(s) due to name collision with source: {Files}", collisions.Count, string.Join(", ", collisions));
            }
        }

        // ───────────────────────────── 1c ──────────────────────────────────────
        private static void MergeSharedContent(string sourceRoot, string targetRoot, List<string> copied)
        {
            string? sharedContentDir = null;
            var probe = Path.GetDirectoryName(sourceRoot);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(probe); i++)
            {
                var candidate = Path.Combine(probe, "SharedContent");
                if (File.Exists(Path.Combine(candidate, "xaml", "Styles.xaml")))
                {
                    sharedContentDir = candidate;
                    break;
                }
                probe = Path.GetDirectoryName(probe);
            }
            if (sharedContentDir is null)
            {
                return;
            }

            var merged = new List<string>();

            // Styles.xaml (required resource dictionary)
            var stylesDst = Path.Combine(targetRoot, "Styles.xaml");
            if (!File.Exists(stylesDst))
            {
                CopyInto(Path.Combine(sharedContentDir, "xaml", "Styles.xaml"), stylesDst);
                copied.Add("Styles.xaml");
                merged.Add("Styles.xaml");
            }

            // MainPage.xaml / .cs (Navigate target)
            foreach (var mp in new[] { "MainPage.xaml", "MainPage.xaml.cs" })
            {
                var src = Path.Combine(sharedContentDir, "cs", mp);
                var dst = Path.Combine(targetRoot, mp);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    CopyInto(src, dst);
                    copied.Add(mp);
                    merged.Add(mp);
                }
            }

            // media/ branding images -> Assets\
            var mediaDir = Path.Combine(sharedContentDir, "media");
            if (Directory.Exists(mediaDir))
            {
                var assetsDst = Path.Combine(targetRoot, "Assets");
                int mediaCopied = 0;
                foreach (var file in Directory.EnumerateFiles(mediaDir))
                {
                    if (!HasCopyExtension(file))
                    {
                        continue;
                    }

                    var dst = Path.Combine(assetsDst, Path.GetFileName(file));
                    if (File.Exists(dst))
                    {
                        continue;
                    }

                    CopyInto(file, dst);
                    copied.Add(Path.Combine("Assets", Path.GetFileName(file)));
                    mediaCopied++;
                }
                if (mediaCopied > 0)
                {
                    merged.Add($"media -> Assets\\ ({mediaCopied} img)");
                }
            }

            if (merged.Count > 0)
            {
                Console.Out.WriteLine($"    Merged {merged.Count} item(s) from SharedContent/ ({sharedContentDir}): {string.Join(", ", merged)}");
            }
        }

        // ───────────────────────────── 1d ──────────────────────────────────────
        private static void RegisterStyles(string targetRoot)
        {
            var stylesFile = Path.Combine(targetRoot, "Styles.xaml");
            var appXamlFile = Path.Combine(targetRoot, "App.xaml");
            if (!File.Exists(stylesFile) || !File.Exists(appXamlFile))
            {
                return;
            }

            var body = File.ReadAllText(appXamlFile);
            if (body.Contains("Source=\"Styles.xaml\"") || body.Contains("Source='Styles.xaml'"))
            {
                return;
            }

            string updated;
            if (MergedDictionariesOpen().IsMatch(body))
            {
                updated = MergedDictionariesOpen().Replace(body,
                    m => m.Value + "\r\n                <ResourceDictionary Source=\"Styles.xaml\"/>", 1);
            }
            else if (AppResourcesOpen().IsMatch(body))
            {
                updated = AppResourcesOpen().Replace(body,
                    m => m.Value + "\r\n            <ResourceDictionary.MergedDictionaries>\r\n                <ResourceDictionary Source=\"Styles.xaml\"/>\r\n            </ResourceDictionary.MergedDictionaries>", 1);
            }
            else
            {
                return;
            }
            File.WriteAllText(appXamlFile, updated);
            Console.Out.WriteLine("    Added Styles.xaml to App.xaml MergedDictionaries");
        }

        // ───────────────────────────── 2 ───────────────────────────────────────
        private void PreserveUwpReferences(string sourceRoot, string targetRoot)
        {
            var refDir = Path.Combine(targetRoot, ".uwp-source");

            var csprojs = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
            foreach (var p in csprojs)
            {
                Directory.CreateDirectory(refDir);
                var dst = Path.Combine(refDir, Path.GetFileName(p) + ".reference");
                File.Copy(p, dst, overwrite: true);
                Console.Out.WriteLine($"    Preserved {Path.GetFileName(p)} as .uwp-source/{Path.GetFileName(p)}.reference (reference only)");
            }
            if (csprojs.Count == 0)
            {
                logger.LogWarning("No .csproj found under source — no reference for the original PackageReference list.");
            }

            var extensions = new List<string>();
            foreach (var mf in Directory.EnumerateFiles(sourceRoot, "*.appxmanifest", SearchOption.TopDirectoryOnly))
            {
                Directory.CreateDirectory(refDir);
                var dst = Path.Combine(refDir, Path.GetFileName(mf) + ".reference");
                File.Copy(mf, dst, overwrite: true);
                Console.Out.WriteLine($"    Preserved {Path.GetFileName(mf)} as .uwp-source/{Path.GetFileName(mf)}.reference (do not overwrite the scaffold manifest)");

                var content = File.ReadAllText(mf);
                foreach (Match m in ManifestExtension().Matches(content))
                {
                    extensions.Add(m.Groups[1].Value);
                }
            }
            if (extensions.Count > 0)
            {
                Console.Out.WriteLine($"    WARNING: UWP manifest declares {extensions.Count} Extension(s): {string.Join(", ", extensions)}");
                Console.Out.WriteLine("      Do NOT copy them verbatim — most UWP manifest extensions have no WinUI 3 desktop equivalent and must be re-implemented or dropped. Run 'winapp migrate analyze --from-uwp' for per-item guidance.");
            }
        }

        // ───────────────────────────── 2b ──────────────────────────────────────
        private void PatchRuntimeIdentifier(string targetRoot)
        {
            var csprojs = EnumerateFiles(targetRoot)
                .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int patched = 0, already = 0;
            foreach (var cp in csprojs)
            {
                var body = File.ReadAllText(cp);
                if (body.Contains(RidFixMarker)) { already++; continue; }
                var m = HostArchRuntimeIdentifier().Match(body);
                if (!m.Success)
                {
                    continue;
                }

                var indent = m.Groups["indent"].Value;
                var injection =
                    $"{indent}{RidFixMarker}\r\n" +
                    $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'x86'\">win-x86</RuntimeIdentifier>\r\n" +
                    $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'x64'\">win-x64</RuntimeIdentifier>\r\n" +
                    $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'ARM64'\">win-arm64</RuntimeIdentifier>\r\n" +
                    m.Value;
                body = body[..m.Index] + injection + body[(m.Index + m.Length)..];
                File.WriteAllText(cp, body);
                patched++;
                Console.Out.WriteLine($"    Patched RuntimeIdentifier in {Path.GetFileName(cp)} — F5 now works on x86/x64/ARM64 hosts");
            }
            if (csprojs.Count == 0)
            {
                logger.LogWarning("No WinUI 3 .csproj found at target — did you run 'dotnet new winui' first?");
            }
            else if (patched == 0 && already > 0)
            {
                Console.Out.WriteLine($"    RuntimeIdentifier already patched in {already} .csproj file(s) — no change");
            }
        }

        // ───────────────────────────── 3 ───────────────────────────────────────
        private static void RewriteNamespaces(string targetRoot)
        {
            var files = EnumerateFiles(targetRoot)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int changed = 0;
            foreach (var f in files)
            {
                var orig = File.ReadAllText(f);
                var updated = orig.Replace("Windows.UI.Xaml", "Microsoft.UI.Xaml");
                if (updated != orig)
                {
                    File.WriteAllText(f, updated);
                    changed++;
                }
            }
            Console.Out.WriteLine($"    Rewrote Windows.UI.Xaml -> Microsoft.UI.Xaml in {changed} of {files.Count} .cs/.xaml files");
        }

        // ───────────────────────────── 4 ───────────────────────────────────────
        private static void NeutralizeFilterProneClasses(string targetRoot, List<string> copied)
        {
            // RootFrameNavigationHelper in Common\NavigationHelper.cs wires ALT+Left /
            // BrowserBack / mouse XButton back-nav via virtual-key reads; the model
            // provider's content-safety filter classifies that as keylogger code and
            // rejects the migration output. Replace the class body with a no-op stub.
            const string className = "RootFrameNavigationHelper";
            const string stubBody =
                "        // No-op stub written by 'winapp migrate scaffold'.\r\n" +
                "        //\r\n" +
                "        // The original UWP implementation hooked accelerator-key activation for\r\n" +
                "        // ALT+Left/Right and BrowserBack/Forward, and pointer events for mouse\r\n" +
                "        // XButton1/XButton2. The WinUI 3 equivalent goes through low-level\r\n" +
                "        // keyboard-state APIs that the model provider's content-safety filter\r\n" +
                "        // rejects. Back-nav is not the demonstrated feature of any UWP SDK\r\n" +
                "        // sample, so we leave the field bound but make activation a no-op.\r\n" +
                "        // If you really need ALT+Left back-nav, add a single\r\n" +
                "        //   <KeyboardAccelerator Key=\"Left\" Modifiers=\"Menu\"/>\r\n" +
                "        // to the AppBarButton or NavigationViewItem that triggers GoBack.";

            foreach (var rel in copied)
            {
                if (!NavigationHelperPath().IsMatch(rel.Replace('/', '\\')))
                {
                    continue;
                }

                var full = Path.Combine(targetRoot, rel);
                if (NeutralizeClass(full, className, stubBody))
                {
                    Console.Out.WriteLine($"    Neutralized class {className} in {rel}");
                }
            }
        }

        private static bool NeutralizeClass(string fullPath, string className, string stubBody)
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var text = File.ReadAllText(fullPath);
            var pattern = $@"(?ms)((?:public\s+|internal\s+)?class\s+{Regex.Escape(className)}\b[^{{]*\{{)";
            var m = Regex.Match(text, pattern);
            if (!m.Success)
            {
                return false;
            }

            int bodyStart = m.Index + m.Length;
            int depth = 1, i = bodyStart;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                }

                i++;
            }
            if (depth != 0)
            {
                return false;
            }

            int bodyEnd = i - 1;

            var before = text[..bodyStart];
            var after = text[bodyEnd..];
            var stubCtor = $"        public {className}(params object[] args) {{ /* no-op; accepts any call shape */ }}\r\n";
            var newText = before + "\r\n" + stubBody + "\r\n" + stubCtor + "    " + after;
            File.WriteAllText(fullPath, newText);
            return true;
        }

        // ───────────────────────────── 5 ───────────────────────────────────────
        private static void WireRootFrame(string targetRoot)
        {
            var mainWindowXaml = FindFile(targetRoot, "MainWindow.xaml");
            var mainWindowCs = FindFile(targetRoot, "MainWindow.xaml.cs");

            bool rootFrameReady = false;
            if (mainWindowXaml is not null)
            {
                var body = File.ReadAllText(mainWindowXaml);
                if (!body.Contains("x:Name=\"RootFrame\"") && !body.Contains(FrameMarker))
                {
                    if (EmptyGridRow1().IsMatch(body))
                    {
                        body = EmptyGridRow1().Replace(body,
                            $"{FrameMarker}\r\n        <Frame x:Name=\"RootFrame\" Grid.Row=\"1\" />", 1);
                        File.WriteAllText(mainWindowXaml, body);
                        Console.Out.WriteLine("    Replaced empty Grid with <Frame x:Name=\"RootFrame\"> in MainWindow.xaml");
                    }
                }
                else if (body.Contains("x:Name=\"RootFrame\""))
                {
                    Console.Out.WriteLine("    MainWindow.xaml already has RootFrame — skipped");
                }

                // Only inject the code-behind Navigate call if a RootFrame is actually present now
                // (either pre-existing or just inserted); otherwise the reference would not compile.
                rootFrameReady = body.Contains("x:Name=\"RootFrame\"");
            }

            if (!rootFrameReady)
            {
                Console.Out.WriteLine("    WARNING: MainWindow.xaml has no <Frame x:Name=\"RootFrame\"> (unrecognized layout) — Navigate injection skipped to avoid a broken build.");
                return;
            }

            if (mainWindowCs is null)
            {
                return;
            }

            var mainPageClass = ResolveMainPageClass(targetRoot);
            if (mainPageClass is null)
            {
                Console.Out.WriteLine("    WARNING: Could not determine MainPage class — Navigate injection skipped");
                return;
            }

            var mwBody = File.ReadAllText(mainWindowCs);
            if (mwBody.Contains(NavMarker) || mwBody.Contains("RootFrame.Navigate"))
            {
                if (mwBody.Contains("RootFrame.Navigate"))
                {
                    Console.Out.WriteLine("    MainWindow.xaml.cs already has RootFrame.Navigate — skipped");
                }

                return;
            }

            var initMatch = InitializeComponentCall().Match(mwBody);
            if (!initMatch.Success)
            {
                return;
            }

            int insertPos = initMatch.Index + initMatch.Length;
            var indent = LeadingWhitespace().Match(initMatch.Value).Groups[1].Value;
            var navCode =
                $"\r\n\r\n{indent}{NavMarker}\r\n" +
                $"{indent}// Defer the initial navigation to the next dispatcher tick so it runs AFTER\r\n" +
                $"{indent}// this constructor returns and App.OnLaunched has assigned the static window\r\n" +
                $"{indent}// reference. Navigating synchronously here would run the target Page's\r\n" +
                $"{indent}// OnNavigatedTo before that assignment completes, causing a null static-window\r\n" +
                $"{indent}// read (E_POINTER / NullReferenceException) crash.\r\n" +
                $"{indent}this.DispatcherQueue.TryEnqueue(() => RootFrame.Navigate(typeof({mainPageClass})));";
            mwBody = mwBody[..insertPos] + navCode + mwBody[insertPos..];
            File.WriteAllText(mainWindowCs, mwBody);
            Console.Out.WriteLine($"    Injected RootFrame.Navigate(typeof({mainPageClass})) into MainWindow.xaml.cs");
        }

        private static string? ResolveMainPageClass(string targetRoot)
        {
            // Strategy A: MainPage.xaml x:Class (in target, then .uwp-source).
            var mainPageXaml = FindFile(targetRoot, "MainPage.xaml");
            if (mainPageXaml is null)
            {
                var uwpSourceDir = Path.Combine(targetRoot, ".uwp-source");
                if (Directory.Exists(uwpSourceDir))
                {
                    mainPageXaml = Directory.EnumerateFiles(uwpSourceDir, "MainPage.xaml", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\"));
                }
            }
            if (mainPageXaml is not null)
            {
                var xClass = XClassAttr().Match(File.ReadAllText(mainPageXaml));
                if (xClass.Success)
                {
                    return xClass.Groups[1].Value;
                }
            }

            // Strategy B: any .cs declaring 'partial class MainPage'.
            foreach (var cs in EnumerateFiles(targetRoot).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var content = File.ReadAllText(cs);
                if (!PartialMainPage().IsMatch(content))
                {
                    continue;
                }

                var ns = NamespaceDecl().Match(content);
                if (ns.Success)
                {
                    return $"{ns.Groups[1].Value}.MainPage";
                }
            }
            return null;
        }

        // ───────────────────────── helpers ─────────────────────────────────────
        private static IEnumerable<string> EnumerateFiles(string root)
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (IsExcluded(f, root))
                {
                    continue;
                }
                yield return f;
            }
        }

        private static bool IsExcluded(string file, string root)
        {
            var rel = Path.GetRelativePath(root, file);
            foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                foreach (var ex in ExcludeDirSegments)
                {
                    if (string.Equals(seg, ex, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasCopyExtension(string file)
        {
            var name = file.ToLowerInvariant();
            foreach (var ext in CopyExtensions)
            {
                if (name.EndsWith(ext, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CopyInto(string src, string dst)
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.Copy(src, dst, overwrite: true);
        }

        private static string? FindFile(string root, string fileName)
        {
            return EnumerateFiles(root).FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ───────────────────────── source-gen regexes ──────────────────────────────
    [GeneratedRegex(@"<ResourceDictionary\.MergedDictionaries>")]
    private static partial Regex MergedDictionariesOpen();

    [GeneratedRegex(@"<Application\.Resources>\s*<ResourceDictionary>")]
    private static partial Regex AppResourcesOpen();

    [GeneratedRegex("(?i)<(?:uap\\d?:)?Extension\\s+Category=\"([^\"]+)\"")]
    private static partial Regex ManifestExtension();

    [GeneratedRegex(@"(?m)^(?<indent>\s*)<RuntimeIdentifier\s+Condition=""'\$\(RuntimeIdentifier\)'\s*==\s*''"">win-\$\(\[System\.Runtime\.InteropServices\.RuntimeInformation\][^<]+</RuntimeIdentifier>\s*$")]
    private static partial Regex HostArchRuntimeIdentifier();

    [GeneratedRegex(@"(^|\\)Common\\NavigationHelper\.cs$")]
    private static partial Regex NavigationHelperPath();

    [GeneratedRegex(@"<Grid\s+Grid\.Row=""1""\s*/>")]
    private static partial Regex EmptyGridRow1();

    [GeneratedRegex(@"(?m)([ \t]*this\.InitializeComponent\(\);|[ \t]*InitializeComponent\(\);)")]
    private static partial Regex InitializeComponentCall();

    [GeneratedRegex(@"^(\s*)")]
    private static partial Regex LeadingWhitespace();

    [GeneratedRegex(@"x:Class=""([^""]+)""")]
    private static partial Regex XClassAttr();

    [GeneratedRegex(@"partial\s+class\s+MainPage\b")]
    private static partial Regex PartialMainPage();

    [GeneratedRegex(@"(?m)^\s*namespace\s+([\w.]+)")]
    private static partial Regex NamespaceDecl();
}
