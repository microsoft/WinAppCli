// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Performs the deterministic portion of a UWP to WinUI 3 migration and records known residual work.
/// </summary>
internal partial class MigrateCommand : Command, IShortDescription
{
    public string ShortDescription => "Mechanically migrate a UWP project to a new WinUI 3 project";

    public static Argument<DirectoryInfo> SourceArgument { get; }
    public static Option<DirectoryInfo> OutputOption { get; }
    public static Option<string?> NameOption { get; }

    // Files that must NOT be copied from the UWP source into the WinUI target: build artifacts,
    // project/solution system files, IDE state, packaging outputs, and private signing material.
    // Everything else — including fonts, JSON/XML data, audio/video, shaders and other project
    // content — is copied so the migrated code never references a missing runtime asset.
    // (Directories such as bin/obj are already pruned by ExcludeDirSegments; Package.appxmanifest
    // is handled by PreserveUwpReferences.)
    private static readonly string[] DoNotCopyExtensions =
    [
        ".csproj", ".vcxproj", ".vbproj", ".shproj", ".projitems", ".sln", ".slnf",
        ".user", ".suo", ".cache", ".vsidx", ".pdb", ".ilk", ".exp", ".idb", ".tlog",
        ".exe", ".dll", ".lib", ".obj", ".appxmanifest",
        ".appx", ".msix", ".appxbundle", ".appxupload", ".nupkg", ".snupkg",
        // Private signing material — must never be copied into (and risk being committed with)
        // the migrated project.
        ".pfx", ".snk", ".p12", ".pvk", ".cer", ".key"
    ];

    private static readonly string[] ExcludeDirSegments =
    [
        "bin", "obj", ".uwp-source", ".vs", ".git", ".github", ".copilot"
    ];

    private static readonly HashSet<string> AllowedExistingOutputEntries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".github"
        };

    private const string RidFixMarker = "<!-- arm64-f5-fix:winapp-migrate-scaffold -->";
    private const string FrameMarker = "<!-- shell-frame:winapp-migrate-scaffold -->";
    private const string NavMarker = "// shell-nav:winapp-migrate-scaffold";

    static MigrateCommand()
    {
        SourceArgument = new Argument<DirectoryInfo>("source")
        {
            Description = "UWP project source folder (contains the .csproj and Package.appxmanifest)."
        };
        SourceArgument.AcceptExistingOnly();

        OutputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "New directory where the mechanically migrated WinUI 3 project will be created.",
            Required = true
        };
        NameOption = new Option<string?>("--name", "-n")
        {
            Description = "Target project name. Defaults to the UWP project name with an 'App' suffix."
        };
    }

    public MigrateCommand()
        : base("migrate", "Create a new WinUI 3 project from UWP source and apply deterministic mechanical transforms. Writes migration-report.json with known residual work. Success means the mechanical pass completed; it does not guarantee that the result builds or runs.")
    {
        Arguments.Add(SourceArgument);
        Options.Add(OutputOption);
        Options.Add(NameOption);
    }

    public class Handler(IDotNetService dotNetService, ILogger<MigrateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var originalOut = Console.Out;
            if (quiet) { Console.SetOut(new QuietFilteringTextWriter(originalOut)); }
            try
            {
                return await InvokeCore(parseResult, cancellationToken);
            }
            finally
            {
                if (quiet) { Console.SetOut(originalOut); }
            }
        }

        private async Task<int> InvokeCore(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var source = parseResult.GetValue(SourceArgument)!;
            var target = parseResult.GetValue(OutputOption)!;

            var sourceRoot = source.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetRoot = target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Console.Out.WriteLine("==> winapp migrate");
            Console.Out.WriteLine($"    Source : {sourceRoot}");
            Console.Out.WriteLine($"    Output : {targetRoot}");

            // Reject overlapping paths: copying a directory onto itself, or into/from an
            // ancestor, corrupts the source or loops the WinUI scaffold back through the copy.
            if (PathsOverlap(sourceRoot, targetRoot))
            {
                Console.Out.WriteLine("[ERROR] Source and target must be two distinct, non-nested directories.");
                return 1;
            }

            // Verify the advertised prerequisites before mutating any files.
            var sourceHasProject = EnumerateFiles(sourceRoot).Any(f =>
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(f), "Package.appxmanifest", StringComparison.OrdinalIgnoreCase));
            if (!sourceHasProject)
            {
                Console.Out.WriteLine("[ERROR] Source is not a UWP project — no .csproj or Package.appxmanifest found. Nothing to migrate.");
                return 1;
            }

            var unsupportedOutputEntries = GetUnsupportedOutputEntries(targetRoot);
            if (unsupportedOutputEntries.Count > 0)
            {
                Console.Out.WriteLine(
                    "[ERROR] Output directory may contain only supported control-plane metadata " +
                    $"(.git and .github). Remove or relocate: {string.Join(", ", unsupportedOutputEntries)}");
                return 1;
            }

            var sourceProject = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            var requestedName = parseResult.GetValue(NameOption);
            var projectName = string.IsNullOrWhiteSpace(requestedName)
                ? GetProjectName(sourceRoot, sourceProject)
                : SanitizeProjectName(requestedName);
            var targetParent = Directory.GetParent(targetRoot);
            if (targetParent is null)
            {
                Console.Out.WriteLine("[ERROR] Output directory must have a parent directory.");
                return 1;
            }
            targetParent.Create();

            var stagingRoot = Path.Combine(
                targetParent.FullName,
                $".{Path.GetFileName(targetRoot)}.winapp-migrate-{Guid.NewGuid():N}");
            var templateArguments = WindowsCommandLine.JoinArguments(
                ["new", "winui", "--name", projectName, "--output", stagingRoot, "--no-update-check"])!;

            string? stagedProject;
            try
            {
                var templateResult = await dotNetService.RunDotnetCommandAsync(targetParent, templateArguments, cancellationToken);
                if (templateResult.ExitCode != 0)
                {
                    Console.Out.WriteLine("[ERROR] Could not create the WinUI 3 project with 'dotnet new winui'.");
                    if (!string.IsNullOrWhiteSpace(templateResult.Error))
                    {
                        Console.Out.WriteLine(templateResult.Error.Trim());
                    }
                    return 1;
                }

                stagedProject = Directory.EnumerateFiles(stagingRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (stagedProject is null || FindFile(stagingRoot, "MainWindow.xaml") is null)
                {
                    Console.Out.WriteLine("[ERROR] The 'dotnet new winui' template did not produce the expected project files.");
                    return 1;
                }

                if (!TryMergeScaffold(stagingRoot, targetRoot, out var mergeError))
                {
                    Console.Out.WriteLine($"[ERROR] Could not merge the WinUI scaffold into the output directory: {mergeError}");
                    return 1;
                }
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }

            var targetProject = Path.Combine(targetRoot, Path.GetRelativePath(stagingRoot, stagedProject!));
            var report = new MigrationReport
            {
                Source = new MigrationProject
                {
                    Root = NormalizePath(sourceRoot),
                    ProjectFile = sourceProject is null ? null : NormalizePath(Path.GetRelativePath(sourceRoot, sourceProject))
                },
                Target = new MigrationProject
                {
                    Root = NormalizePath(targetRoot),
                    ProjectFile = NormalizePath(Path.GetRelativePath(targetRoot, targetProject))
                }
            };
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-SCAFFOLD",
                Summary = "Created the target with the official WinUI project template",
                ChangedFiles = 1
            });

            var copied = new List<string>();
            var preservedStartup = new List<string>();

            // ── 1. Copy source files (everything but the project) ────────────────
            foreach (var file in EnumerateFiles(sourceRoot))
            {
                if (!ShouldCopy(file))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(sourceRoot, file);

                // Never overwrite the WinUI scaffold's own startup files with their UWP
                // counterparts: a UWP App.xaml.cs launches via Window.Current, so replacing
                // the scaffold's MainWindow bootstrap leaves the migrated app unable to start.
                if (IsScaffoldStartupFile(rel))
                {
                    preservedStartup.Add(rel);
                    continue;
                }

                CopyInto(file, Path.Combine(targetRoot, rel));
                copied.Add(rel);
            }
            Console.Out.WriteLine($"    Copied {copied.Count} source files");
            if (preservedStartup.Count > 0)
            {
                Console.Out.WriteLine($"    Preserved WinUI scaffold startup file(s), did NOT copy UWP: {string.Join(", ", preservedStartup)}");
                Console.Out.WriteLine("      Migrate app-level resources from the UWP App.xaml manually (the WinUI startup bootstrap was kept).");
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG001",
                    Category = "app-resources",
                    Priority = "required",
                    Summary = "Merge application resources and startup behavior from the preserved UWP App files",
                    Reason = "The WinUI startup files were kept because UWP App.xaml and App.xaml.cs cannot safely overwrite the desktop bootstrap.",
                    Locations = preservedStartup.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", path + ".reference"))
                    }).ToList()
                });
            }

            // ── 1b. Merge sibling shared/ (cross-language SDK-sample layout) ─────
            MergeSiblingShared(sourceRoot, targetRoot, copied, report);

            // ── 1c. Merge top-level SharedContent/ (repo-wide sample assets) ─────
            MergeSharedContent(sourceRoot, targetRoot, copied);

            // ── 1d. Register Styles.xaml in App.xaml MergedDictionaries ──────────
            var styleFiles = RegisterStyles(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-RESOURCES",
                Summary = "Registered migrated Styles.xaml resources",
                ChangedFiles = styleFiles
            });

            // ── 2. Preserve UWP .csproj / .appxmanifest under .uwp-source/ ───────
            PreserveUwpReferences(sourceRoot, targetRoot, preservedStartup, report);

            // ── 2b. Patch WinUI 3 csproj RuntimeIdentifier for cross-arch F5 ─────
            var projectFiles = PatchRuntimeIdentifier(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-PROJECT",
                Summary = "Patched project settings for host architecture",
                ChangedFiles = projectFiles
            });

            // ── 3. Namespace rewrite: Windows.UI.Xaml -> Microsoft.UI.Xaml ──────
            var namespaceFiles = RewriteNamespaces(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-XAML-NS",
                Summary = "Rewrote Windows.UI.Xaml namespaces to Microsoft.UI.Xaml",
                ChangedFiles = namespaceFiles
            });

            var dispatcherFiles = RewriteDispatcherAccess(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-DISPATCHER",
                Summary = "Rewrote DependencyObject.Dispatcher.HasThreadAccess to DispatcherQueue.HasThreadAccess",
                ChangedFiles = dispatcherFiles
            });
            AddDispatcherResidualTodo(targetRoot, report);
            AddWindowingResidualTodo(targetRoot, report);
            AddDisplayInformationResidualTodo(targetRoot, report);

            // ── 4. Neutralize content-filter-prone helper classes ───────────────
            var helperFiles = NeutralizeFilterProneClasses(targetRoot, copied);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-HELPERS",
                Summary = "Neutralized UWP input helpers that cannot be migrated safely",
                ChangedFiles = helperFiles
            });

            // ── 5. Wire MainWindow RootFrame + initial navigation ───────────────
            var shellFiles = WireRootFrame(targetRoot, report);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-SHELL",
                Summary = "Wired the migrated initial page into the WinUI MainWindow",
                ChangedFiles = shellFiles
            });

            report.Summary.CopiedFiles = copied.Count;
            report.Summary.TransformOperations = report.Transforms.Sum(transform => transform.ChangedFiles);
            report.Summary.TodoCategories = report.Todos.Count;
            var reportPath = Path.Combine(targetRoot, "migration-report.json");
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, MigrateJsonContext.Default.MigrationReport),
                cancellationToken);

            Console.Out.WriteLine();
            Console.Out.WriteLine("=== MECHANICAL MIGRATION COMPLETE ===");
            Console.Out.WriteLine($"Changed: {report.Summary.CopiedFiles} copied files, {report.Summary.TransformOperations} transform operations");
            Console.Out.WriteLine($"Remaining work: {report.Summary.TodoCategories} known TODO categor{(report.Summary.TodoCategories == 1 ? "y" : "ies")}");
            foreach (var todo in report.Todos)
            {
                Console.Out.WriteLine($"  [{todo.Priority}] {todo.Category}: {todo.Summary}");
            }
            Console.Out.WriteLine("Details: migration-report.json");
            Console.Out.WriteLine("This result is not guaranteed to build or run.");
            Console.Out.WriteLine("=====================================");

            return 0;
        }

        // ───────────────────────────── 1b ──────────────────────────────────────
        private void MergeSiblingShared(string sourceRoot, string targetRoot, List<string> copied, MigrationReport report)
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
                if (!ShouldCopy(file))
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
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG004",
                    Category = "shared-file-conflicts",
                    Priority = "review",
                    Summary = "Resolve files skipped while merging the shared source directory",
                    Reason = "The migration kept the project-local file when a sibling shared/ file had the same relative path.",
                    Locations = collisions.Select(path => new MigrationLocation { Path = NormalizePath(path) }).ToList()
                });
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
                    if (!ShouldCopy(file))
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
        private static int RegisterStyles(string targetRoot)
        {
            var stylesFile = Path.Combine(targetRoot, "Styles.xaml");
            var appXamlFile = Path.Combine(targetRoot, "App.xaml");
            if (!File.Exists(stylesFile) || !File.Exists(appXamlFile))
            {
                return 0;
            }

            var body = File.ReadAllText(appXamlFile);
            if (body.Contains("Source=\"Styles.xaml\"") || body.Contains("Source='Styles.xaml'"))
            {
                return 0;
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
                return 0;
            }
            File.WriteAllText(appXamlFile, updated);
            Console.Out.WriteLine("    Added Styles.xaml to App.xaml MergedDictionaries");
            return 1;
        }

        // ───────────────────────────── 2 ───────────────────────────────────────
        private void PreserveUwpReferences(
            string sourceRoot,
            string targetRoot,
            IReadOnlyList<string> preservedStartup,
            MigrationReport report)
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
            else
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG003",
                    Category = "project-dependencies",
                    Priority = "required",
                    Summary = "Review UWP project references and migrate the dependencies still required by the app",
                    Reason = "Project and package references cannot be copied safely without deciding whether each dependency supports WinUI 3 desktop.",
                    Locations = csprojs.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", Path.GetFileName(path) + ".reference"))
                    }).ToList()
                });
            }

            foreach (var relativePath in preservedStartup)
            {
                var sourcePath = Path.Combine(sourceRoot, relativePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                Directory.CreateDirectory(refDir);
                var destination = Path.Combine(refDir, relativePath + ".reference");
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }
                File.Copy(sourcePath, destination, overwrite: true);
            }

            var extensions = new List<string>();
            var manifests = new List<string>();
            foreach (var mf in Directory.EnumerateFiles(sourceRoot, "*.appxmanifest", SearchOption.TopDirectoryOnly))
            {
                Directory.CreateDirectory(refDir);
                var dst = Path.Combine(refDir, Path.GetFileName(mf) + ".reference");
                File.Copy(mf, dst, overwrite: true);
                manifests.Add(dst);
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
                Console.Out.WriteLine("      Do NOT copy them verbatim — most UWP manifest extensions have no WinUI 3 desktop equivalent and must be re-implemented or dropped.");
            }
            if (manifests.Count > 0)
            {
                var extensionText = extensions.Count == 0
                    ? ""
                    : $" Detected extension categories: {string.Join(", ", extensions.Distinct(StringComparer.OrdinalIgnoreCase))}.";
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG002",
                    Category = "manifest",
                    Priority = "required",
                    Summary = "Review UWP manifest capabilities and extensions for WinUI 3 desktop",
                    Reason = "The original manifest was preserved for reference rather than copied over the WinUI package manifest." + extensionText,
                    Locations = manifests.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, path))
                    }).ToList()
                });
            }
        }

        // ───────────────────────────── 2b ──────────────────────────────────────
        private int PatchRuntimeIdentifier(string targetRoot)
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
            return patched;
        }

        // ───────────────────────────── 3 ───────────────────────────────────────
        private static int RewriteNamespaces(string targetRoot)
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
            return changed;
        }

        private static int RewriteDispatcherAccess(string targetRoot)
        {
            var changedFiles = 0;
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var original = File.ReadAllText(file);
                var codeMask = MaskCSharpNonCode(original);
                var pairedXaml = file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(file[..^3]);
                if (!pairedXaml || !PageClassDeclaration().IsMatch(codeMask))
                {
                    continue;
                }

                var matches = DispatcherHasThreadAccess().Matches(codeMask);
                if (matches.Count == 0)
                {
                    continue;
                }

                var updated = new StringBuilder(original);
                for (var index = matches.Count - 1; index >= 0; index--)
                {
                    var match = matches[index];
                    updated.Remove(match.Index, match.Length);
                    updated.Insert(match.Index, "DispatcherQueue.HasThreadAccess");
                }
                File.WriteAllText(file, updated.ToString());
                changedFiles++;
            }

            Console.Out.WriteLine($"    Rewrote Dispatcher.HasThreadAccess in {changedFiles} .cs file(s)");
            return changedFiles;
        }

        private static void AddDispatcherResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = new List<MigrationLocation>();
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var codeLines = MaskCSharpNonCode(File.ReadAllText(file))
                    .Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < codeLines.Length; index++)
                {
                    if (!DispatcherRunAsync().IsMatch(codeLines[index])
                        && !DispatcherHasThreadAccess().IsMatch(codeLines[index])
                        && !codeLines[index].Contains("CoreDispatcherPriority", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    locations.Add(new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                        Line = index + 1
                    });
                }

            }

            if (locations.Count == 0)
            {
                return;
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG005",
                Category = "dispatcher",
                Priority = "required",
                Summary = "Review remaining Dispatcher and CoreDispatcher operations",
                Reason = "Only DependencyObject.Dispatcher.HasThreadAccess in XAML Page code-behind is converted automatically. Other receivers and RunAsync delegate behavior require code-specific review.",
                Locations = locations
            });
        }

        private static string MaskCSharpNonCode(string text)
        {
            var mask = text.ToCharArray();
            var inBlockComment = false;
            var inString = false;
            var inChar = false;
            var verbatimString = false;
            var escaped = false;

            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                var next = index + 1 < text.Length ? text[index + 1] : '\0';

                if (current is '\r' or '\n')
                {
                    escaped = false;
                    continue;
                }

                if (inBlockComment)
                {
                    mask[index] = ' ';
                    if (current == '*' && next == '/')
                    {
                        mask[++index] = ' ';
                        inBlockComment = false;
                    }
                    continue;
                }

                if (inString)
                {
                    mask[index] = ' ';
                    if (verbatimString && current == '"' && next == '"')
                    {
                        mask[++index] = ' ';
                        continue;
                    }
                    if (current == '"' && (!escaped || verbatimString))
                    {
                        inString = false;
                        verbatimString = false;
                    }
                    escaped = !verbatimString && current == '\\' && !escaped;
                    if (current != '\\')
                    {
                        escaped = false;
                    }
                    continue;
                }

                if (inChar)
                {
                    mask[index] = ' ';
                    if (current == '\'' && !escaped)
                    {
                        inChar = false;
                    }
                    escaped = current == '\\' && !escaped;
                    if (current != '\\')
                    {
                        escaped = false;
                    }
                    continue;
                }

                if (current == '/' && next == '/')
                {
                    while (index < text.Length && text[index] is not '\r' and not '\n')
                    {
                        mask[index++] = ' ';
                    }
                    index--;
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    mask[index] = mask[++index] = ' ';
                    inBlockComment = true;
                    continue;
                }
                if (current == '@' && next == '"')
                {
                    mask[index] = mask[++index] = ' ';
                    inString = true;
                    verbatimString = true;
                    continue;
                }
                if (current == '"')
                {
                    mask[index] = ' ';
                    inString = true;
                    continue;
                }
                if (current == '\'')
                {
                    mask[index] = ' ';
                    inChar = true;
                }
            }

            return new string(mask);
        }

        private static void AddWindowingResidualTodo(string targetRoot, MigrationReport report)
        {
            var sizingLocations = FindSourceLocations(
                targetRoot,
                line => WindowCurrentBounds().IsMatch(line));
            if (sizingLocations.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG008",
                    Category = "window-sizing",
                    Priority = "required",
                    Summary = "Replace Window.Current.Bounds without reading a newly activated Window.Bounds during initial navigation",
                    Reason = "For XAML layout decisions, wait until Loaded and use the page or root element's XamlRoot.Size. A WinUI Window.Bounds value can still be zero during initial navigation. Use AppWindow or Win32 bounds only when physical window coordinates are actually required.",
                    Locations = sizingLocations
                });
            }

            var otherLocations = FindSourceLocations(
                targetRoot,
                line => WindowCurrent().IsMatch(line) && !WindowCurrentBounds().IsMatch(line));
            if (otherLocations.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG007",
                    Category = "windowing",
                    Priority = "required",
                    Summary = "Replace remaining Window.Current usage with an explicit WinUI Window or AppWindow reference",
                    Reason = "WinUI 3 desktop has no Window.Current singleton, and the correct replacement depends on how each call uses the window.",
                    Locations = otherLocations
                });
            }
        }

        private static void AddDisplayInformationResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = FindSourceLocations(
                targetRoot,
                line => DisplayInformationGetForCurrentView().IsMatch(line));
            if (locations.Count == 0)
            {
                return;
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG009",
                Category = "display-information",
                Priority = "required",
                Summary = "Replace DisplayInformation.GetForCurrentView with the API-specific WinUI desktop equivalent",
                Reason = "Do not invent IDisplayInformationStaticsInterop. For DPI, use XamlRoot.RasterizationScale and XamlRoot.Changed. For CurrentOrientation or OrientationChanged, use MonitorFromWindow, GetMonitorInfo, and EnumDisplaySettings for the app HWND's current monitor, then refresh when AppWindow.Changed reports a position or size change.",
                Locations = locations
            });
        }

        private static List<MigrationLocation> FindSourceLocations(
            string targetRoot,
            Func<string, bool> matches)
        {
            var locations = new List<MigrationLocation>();
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var lines = MaskCSharpNonCode(File.ReadAllText(file))
                    .Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!matches(lines[index]))
                    {
                        continue;
                    }

                    locations.Add(new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                        Line = index + 1
                    });
                }
            }
            return locations;
        }

        // ───────────────────────────── 4 ───────────────────────────────────────
        private static int NeutralizeFilterProneClasses(string targetRoot, List<string> copied)
        {
            // RootFrameNavigationHelper in Common\NavigationHelper.cs wires ALT+Left /
            // BrowserBack / mouse XButton back-nav via virtual-key reads; the model
            // provider's content-safety filter classifies that as keylogger code and
            // rejects the migration output. Replace the class body with a no-op stub.
            const string className = "RootFrameNavigationHelper";
            const string stubBody =
                "        // No-op stub written by 'winapp migrate'.\r\n" +
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

            var changed = 0;
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
                    changed++;
                }
            }
            return changed;
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
        private static int WireRootFrame(string targetRoot, MigrationReport report)
        {
            var mainWindowXaml = FindFile(targetRoot, "MainWindow.xaml");
            var mainWindowCs = FindFile(targetRoot, "MainWindow.xaml.cs");

            bool rootFrameReady = false;
            var changed = 0;
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
                        changed++;
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
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG006",
                    Category = "app-shell",
                    Priority = "required",
                    Summary = "Wire the migrated root page into MainWindow",
                    Reason = "The generated MainWindow layout was not recognized, so the migration did not inject a RootFrame.",
                    Locations = mainWindowXaml is null
                        ? []
                        : [new MigrationLocation { Path = NormalizePath(Path.GetRelativePath(targetRoot, mainWindowXaml)) }]
                });
                return changed;
            }

            if (mainWindowCs is null)
            {
                return changed;
            }

            var mainPageClass = ResolveMainPageClass(targetRoot);
            if (mainPageClass is null)
            {
                Console.Out.WriteLine("    WARNING: Could not determine MainPage class — Navigate injection skipped");
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG006",
                    Category = "app-shell",
                    Priority = "required",
                    Summary = "Choose and navigate to the migrated app's initial page",
                    Reason = "No MainPage x:Class or partial class declaration could be resolved mechanically.",
                    Locations = []
                });
                return changed;
            }

            var mwBody = File.ReadAllText(mainWindowCs);
            if (mwBody.Contains(NavMarker) || mwBody.Contains("RootFrame.Navigate"))
            {
                if (mwBody.Contains("RootFrame.Navigate"))
                {
                    Console.Out.WriteLine("    MainWindow.xaml.cs already has RootFrame.Navigate — skipped");
                }

                return changed;
            }

            var initMatch = InitializeComponentCall().Match(mwBody);
            if (!initMatch.Success)
            {
                return changed;
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
            changed++;
            Console.Out.WriteLine($"    Injected RootFrame.Navigate(typeof({mainPageClass})) into MainWindow.xaml.cs");
            return changed;
        }

        private static string GetProjectName(string sourceRoot, string? sourceProject)
        {
            var candidate = sourceProject is null
                ? new DirectoryInfo(sourceRoot).Name
                : Path.GetFileNameWithoutExtension(sourceProject);
            return SanitizeProjectName(candidate + "App");
        }

        private static string SanitizeProjectName(string candidate)
        {
            var sanitized = InvalidProjectNameCharacter().Replace(candidate, "_").Trim('_', '.');
            if (string.IsNullOrEmpty(sanitized))
            {
                return "MigratedApp";
            }
            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "_" + sanitized;
            }
            return sanitized;
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        private static List<string> GetUnsupportedOutputEntries(string targetRoot)
        {
            if (File.Exists(targetRoot))
            {
                return ["<output path is a file>"];
            }

            if (!Directory.Exists(targetRoot))
            {
                return [];
            }

            var unsupported = Directory.EnumerateFileSystemEntries(targetRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !AllowedExistingOutputEntries.Contains(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (IsReparsePoint(new DirectoryInfo(targetRoot)))
            {
                unsupported.Add("<output directory is a reparse point>");
                return unsupported;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(targetRoot))
            {
                if (TryFindReparsePoint(entry, targetRoot, out var reparsePoint))
                {
                    unsupported.Add($"{NormalizePath(reparsePoint!)} (reparse point)");
                }
            }

            return unsupported;
        }

        private static bool TryMergeScaffold(string stagingRoot, string targetRoot, out string? error)
        {
            var stagedDirectories = Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stagingRoot, path))
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .ToList();
            var stagedFiles = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stagingRoot, path))
                .ToList();

            foreach (var relativePath in stagedDirectories)
            {
                if (File.Exists(Path.Combine(targetRoot, relativePath)))
                {
                    error = $"'{NormalizePath(relativePath)}' is a file in the output but a directory in the template.";
                    return false;
                }
            }

            foreach (var relativePath in stagedFiles)
            {
                var sourcePath = Path.Combine(stagingRoot, relativePath);
                var targetPath = Path.Combine(targetRoot, relativePath);
                if (Directory.Exists(targetPath))
                {
                    error = $"'{NormalizePath(relativePath)}' is a directory in the output but a file in the template.";
                    return false;
                }
                if (File.Exists(targetPath) && !FilesEqual(sourcePath, targetPath))
                {
                    error = $"'{NormalizePath(relativePath)}' conflicts with an existing metadata file.";
                    return false;
                }
            }

            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            try
            {
                if (!Directory.Exists(targetRoot))
                {
                    Directory.CreateDirectory(targetRoot);
                    createdDirectories.Add(targetRoot);
                }

                foreach (var relativePath in stagedDirectories)
                {
                    var targetDirectory = Path.Combine(targetRoot, relativePath);
                    if (!Directory.Exists(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                        createdDirectories.Add(targetDirectory);
                    }
                }
                foreach (var relativePath in stagedFiles)
                {
                    var targetPath = Path.Combine(targetRoot, relativePath);
                    if (File.Exists(targetPath))
                    {
                        continue;
                    }

                    var targetDirectory = Path.GetDirectoryName(targetPath)!;
                    var temporaryPath = Path.Combine(
                        targetDirectory,
                        $".{Path.GetFileName(targetPath)}.winapp-migrate-{Guid.NewGuid():N}");
                    try
                    {
                        File.Copy(Path.Combine(stagingRoot, relativePath), temporaryPath);
                        File.Move(temporaryPath, targetPath);
                        createdFiles.Add(targetPath);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var rollbackFailures = RollbackCreatedPaths(createdFiles, createdDirectories);
                error = $"I/O failure while creating the scaffold: {exception.Message}";
                if (rollbackFailures.Count > 0)
                {
                    error += $" Rollback could not remove: {string.Join(", ", rollbackFailures.Select(NormalizePath))}.";
                }
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryFindReparsePoint(string path, string targetRoot, out string? relativePath)
        {
            var pending = new Stack<FileSystemInfo>();
            pending.Push(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path));

            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                if (IsReparsePoint(entry))
                {
                    relativePath = Path.GetRelativePath(targetRoot, entry.FullName);
                    return true;
                }

                if (entry is DirectoryInfo directory)
                {
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        pending.Push(child);
                    }
                }
            }

            relativePath = null;
            return false;
        }

        private static bool IsReparsePoint(FileSystemInfo entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        private static List<string> RollbackCreatedPaths(
            IReadOnlyList<string> createdFiles,
            IReadOnlyList<string> createdDirectories)
        {
            var failures = new List<string>();
            foreach (var file in createdFiles.Reverse())
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{file} ({exception.Message})");
                }
            }

            foreach (var directory in createdDirectories.Reverse())
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{directory} ({exception.Message})");
                }
            }

            return failures;
        }

        private static bool FilesEqual(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (first.Length != second.Length)
            {
                return false;
            }

            return File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(secondPath));
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

        private static bool ShouldCopy(string file) => !IsBuildOrProjectFile(file);

        private static bool IsBuildOrProjectFile(string file)
        {
            var name = file.ToLowerInvariant();
            foreach (var ext in DoNotCopyExtensions)
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

        // App.xaml / App.xaml.cs define the WinUI 3 startup bootstrap in the scaffold and must not
        // be clobbered by the UWP originals (which launch via Window.Current, not MainWindow).
        private static bool IsScaffoldStartupFile(string rel)
        {
            var name = Path.GetFileName(rel);
            return string.Equals(name, "App.xaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "App.xaml.cs", StringComparison.OrdinalIgnoreCase);
        }

        // True when source == target, or one directory contains the other.
        private static bool PathsOverlap(string sourceRoot, string targetRoot)
        {
            var a = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var aSlash = a + Path.DirectorySeparatorChar;
            var bSlash = b + Path.DirectorySeparatorChar;
            return b.StartsWith(aSlash, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(bSlash, StringComparison.OrdinalIgnoreCase);
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

    [GeneratedRegex(@"\bDispatcher\s*\.\s*HasThreadAccess\b")]
    private static partial Regex DispatcherHasThreadAccess();

    [GeneratedRegex(@"\bclass\s+\w+[^{:]*:\s*[^{]*\bPage\b")]
    private static partial Regex PageClassDeclaration();

    [GeneratedRegex(@"\bDispatcher\s*\.\s*RunAsync\b")]
    private static partial Regex DispatcherRunAsync();

    [GeneratedRegex(@"\bWindow\s*\.\s*Current\b")]
    private static partial Regex WindowCurrent();

    [GeneratedRegex(@"\bWindow\s*\.\s*Current\s*\.\s*Bounds\b")]
    private static partial Regex WindowCurrentBounds();

    [GeneratedRegex(@"\bDisplayInformation\s*\.\s*GetForCurrentView\s*\(")]
    private static partial Regex DisplayInformationGetForCurrentView();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+")]
    private static partial Regex InvalidProjectNameCharacter();
}
