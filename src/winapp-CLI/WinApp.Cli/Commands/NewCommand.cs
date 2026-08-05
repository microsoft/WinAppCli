// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class NewCommand : Command, IShortDescription
{
    public string ShortDescription => "Create a new WinUI app";

    /// <summary>NuGet package id of the official WASDK WinUI C# template pack.</summary>
    internal const string TemplatePackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";

    /// <summary>
    /// Short name of the default template (the blank app) used when the caller doesn't pick one and
    /// no prompt is shown. Resolved against the live template list, so if the pack ever renames it we
    /// fall back to the first project template rather than failing.
    /// </summary>
    internal const string DefaultTemplateShortName = "winui";

    /// <summary><c>--template-version latest</c>: install the newest published pack, skip the update prompt.</summary>
    internal const string LatestVersionKeyword = "latest";

    /// <summary><c>--template-version installed</c>: keep whatever pack is installed, skip the feed check and prompt.</summary>
    internal const string InstalledVersionKeyword = "installed";

    /// <summary>Minimum .NET SDK version, matching <c>winapp run</c>'s project mode.</summary>
    internal static readonly Version MinimumSdkVersion = new(8, 0, 100);

    // Starting with .NET SDK 9.0.200, `dotnet new install` requires the `PackageId@Version` form and
    // rejects the older `PackageId::Version` syntax; SDKs below this band still use `::`. The install
    // command picks the separator from the detected SDK so a cold-cache first run works on both.
    internal static readonly Version FirstAtSeparatorSdkVersion = new(9, 0, 200);

    // Fixed dotnet argument lists, hoisted to static readonly fields to satisfy CA1861.
    private static readonly string[] VersionArgs = ["--version"];
    private static readonly string[] ListTemplatePacksArgs = ["new", "uninstall"];
    private static readonly string[] UpdateCheckArgs = ["new", "update", "--check-only"];

    // `dotnet new list winui --columns-all`: enumerate the installed WinUI templates with the Type and
    // Tags columns we classify on. `list` is context-aware — run from inside a WinUI project it also
    // includes item templates (Type=item); elsewhere it lists only project templates.
    private static readonly string[] ListTemplatesArgs = ["new", "list", "winui", "--columns-all"];

    // Force English CLI output for the template subprocesses we parse. `dotnet new uninstall`,
    // `list`, and `update` all localize their labels/headers per DOTNET_CLI_UI_LANGUAGE, which would
    // break the locale-dependent parsing (installed-version detection, column headers, update rows).
    private static readonly Dictionary<string, string> EnglishUiEnvironment = new()
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
    };

    // Distinct exit codes so agents can branch on the failing stage without parsing text.
    internal const int ExitSuccess = 0;
    internal const int ExitInvalidArgs = 2;
    internal const int ExitSdkMissing = 3;
    internal const int ExitTemplatePackFailed = 4;
    internal const int ExitScaffoldFailed = 5;

    // Windows caps a single path component at 255 characters. The scaffold writes "<name>.csproj",
    // so the project name must leave room for that suffix or dotnet new fails mid-scaffold instead of
    // failing fast on the invalid argument.
    internal const int MaxProjectNameLength = 255 - 7; // ".csproj".Length == 7

    public static Option<string?> TemplateOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<DirectoryInfo?> OutputOption { get; }
    public static Option<bool> UseDefaultsOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<string?> TemplateVersionOption { get; }
    public static Option<bool> ListOption { get; }

    static NewCommand()
    {
        TemplateOption = new Option<string?>("--template", "-t")
        {
            // Dynamic: the accepted values come from the installed pack, not a fixed enum, so the CLI
            // picks up new templates automatically. Validated against the live list at run time.
            Description = "Template short name (e.g. winui, winui-navview, winui-mvvm, winui-lib, winui-unittest). Run 'winapp new --list' to see all.",
            HelpName = "short-name"
        };
        NameOption = new Option<string?>("--name", "-n")
        {
            Description = "Name for the new app/project (default: derived from --output, else 'WinUIApp')."
        };
        OutputOption = new Option<DirectoryInfo?>("--output", "-o")
        {
            Description = "Directory to create the app in (default: ./<name>). Created if it doesn't exist."
        };
        UseDefaultsOption = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt; use defaults (blank template, name from --output/--name, keep installed templates)."
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Scaffold even if the output directory already contains files."
        };
        TemplateVersionOption = new Option<string?>("--template-version")
        {
            Description = $"WinUI template pack version: '{LatestVersionKeyword}' (install newest), '{InstalledVersionKeyword}' (keep what's installed), or an explicit version. Default: install latest if none, else prompt to update a stale pack."
        };
        ListOption = new Option<bool>("--list")
        {
            Description = "List the available WinUI templates and exit (installs the latest template pack if none is installed)."
        };
    }

    public NewCommand() : base("new", "Create a new WinUI app from an official Windows App SDK template. Interactive by default: pick a template, then a name (the output directory defaults to ./<name>). Automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Requires the .NET SDK; installs the WinUI template pack on demand (grabbing the latest, or offering to update a stale one) and delegates scaffolding to 'dotnet new'. Use --list to see the available templates. Scaffolds against the installed SDK's target framework and prints a template-specific next step when done (e.g. 'dotnet run' for app templates).")
    {
        Options.Add(TemplateOption);
        Options.Add(NameOption);
        Options.Add(OutputOption);
        Options.Add(UseDefaultsOption);
        Options.Add(ForceOption);
        Options.Add(TemplateVersionOption);
        Options.Add(ListOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>
    /// Emits the <see cref="NewCommandResult"/> JSON shape for a parse-time failure (e.g. an invalid
    /// boolean value or a single-dash typo) so <c>winapp new --json</c> callers still get
    /// machine-readable output instead of System.CommandLine's help/error text. Invoked from the
    /// Program-level parse-error bridge before the handler would otherwise run.
    /// </summary>
    internal static void EmitParseErrorJson(string error, TextWriter? output = null)
    {
        var result = new NewCommandResult
        {
            Created = false,
            Error = error,
        };
        var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewCommandResult);
        (output ?? Console.Out).WriteLine(json);
    }

    /// <summary>
    /// Returns true only when <paramref name="name"/> is a safe single path segment. The resolved
    /// name becomes both the default output directory (<c>./&lt;name&gt;</c>) and the <c>dotnet new</c>
    /// project name, so path separators, <c>.</c>/<c>..</c>, rooted paths, invalid filename
    /// characters, Windows reserved device names, over-long names (that would push the generated
    /// <c>&lt;name&gt;.csproj</c> past the 255-character path-component limit), and trailing dot/space
    /// names are rejected so the scaffold can't escape the current directory or produce an unusable
    /// folder.
    /// </summary>
    internal static bool IsValidProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > MaxProjectNameLength
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Reject option-shaped names. Even though every token is passed to dotnet new via ArgumentList
        // (so nothing is re-parsed by a shell), a leading '-' makes the child dotnet new parser treat
        // the value as another switch — e.g. a name of "--force" derived from "--output .\--force" would
        // be consumed as dotnet new's own --force flag rather than the project name.
        if (name[0] is '-')
        {
            return false;
        }

        // Windows cannot create directories whose name ends in a space or dot.
        if (name[^1] is ' ' or '.')
        {
            return false;
        }

        // Reserved DOS device names are invalid regardless of any extension (e.g. "CON", "CON.txt").
        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        return !ReservedDeviceNames.Contains(stem);
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns true only when the <paramref name="requestedVersion"/> of the template pack is already
    /// installed. Parses the <c>dotnet new uninstall</c> listing (a package-id line followed by an
    /// indented "Version: x" line) so an older/different installed version is not mistaken for the
    /// requested one.
    /// </summary>
    internal static bool IsTemplatePackInstalled(string uninstallListOutput, string requestedVersion)
        => TryGetInstalledPackVersion(uninstallListOutput, out var installed)
            && installed is not null
            && NuGetVersionHelper.NuGetVersionsEquivalent(installed, requestedVersion);

    /// <summary>
    /// Extracts the installed version of the WinUI template pack from a <c>dotnet new uninstall</c>
    /// listing (the "Version: x" line nested under the package-id header). Returns false when the pack
    /// is not present. Used to distinguish an older installed version (which may be upgraded) from a
    /// newer one (which must not be silently downgraded, since template packs are global).
    /// </summary>
    internal static bool TryGetInstalledPackVersion(string uninstallListOutput, out string? installedVersion)
    {
        installedVersion = null;
        if (string.IsNullOrEmpty(uninstallListOutput))
        {
            return false;
        }

        var lines = uninstallListOutput.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals(TemplatePackageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // `dotnet new uninstall` nests details under the package header, e.g.
            //    Microsoft.WindowsAppSDK.WinUI.CSharp.Templates   (indent 3)
            //       Version: 0.0.6-alpha                          (indent 6)
            // so the version is a deeper-indented line. Bound the scan to this block by stopping at
            // the next line indented no deeper than the header, which prevents a following package's
            // "Version:" line from being misread as the WinUI pack version.
            var headerIndent = IndentWidth(lines[i]);
            const string marker = "Version:";
            for (var j = i + 1; j < lines.Length; j++)
            {
                var raw = lines[j];
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (IndentWidth(raw) <= headerIndent)
                {
                    break;
                }

                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    installedVersion = line[(idx + marker.Length)..].Trim();
                    return installedVersion.Length > 0;
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>Number of leading space/tab characters (indentation) on a line, ignoring a trailing CR.</summary>
    private static int IndentWidth(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count;
    }

    public class Handler(
        IDotNetService dotNetService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ILogger<Handler> logger) : AsynchronousCommandLineAction
    {
        /// <summary>How the caller asked us to resolve the template-pack version.</summary>
        private enum VersionMode
        {
            /// <summary>No <c>--template-version</c>: install latest if none, else prompt to update a stale pack.</summary>
            Default,
            /// <summary><c>latest</c>: install the newest pack, no prompt.</summary>
            Latest,
            /// <summary><c>installed</c>: keep the installed pack, no feed check or prompt.</summary>
            Installed,
            /// <summary>An explicit version was requested.</summary>
            Explicit,
        }

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var templateArg = parseResult.GetValue(TemplateOption);
            var nameFromOption = parseResult.GetValue(NameOption);
            var output = parseResult.GetValue(OutputOption);
            var useDefaults = parseResult.GetValue(UseDefaultsOption);
            var force = parseResult.GetValue(ForceOption);
            var versionRaw = parseResult.GetValue(TemplateVersionOption);
            var list = parseResult.GetValue(ListOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);

            var name = nameFromOption;

            // Non-interactive environments (CI, piped stdin) behave like --use-defaults to avoid
            // prompt exceptions — mirrors InitCommand. --json also implies the no-prompt path: Spectre
            // prompt bytes written to stdout would otherwise precede the JSON payload and break
            // JSON.parse for machine callers, even on an interactive TTY.
            if (!useDefaults && (isJson || !ansiConsole.Profile.Capabilities.Interactive))
            {
                if (!isJson)
                {
                    logger.LogWarning("{Warning}  Non-interactive environment detected. Using default values.", UiSymbols.Warning);
                }
                useDefaults = true;
            }

            // Classify --template-version. Keywords (latest/installed) are matched case-insensitively
            // and bypass the numeric-version grammar; only an explicit version is shape-validated.
            var (mode, explicitVersion) = ClassifyVersion(versionRaw);
            if (mode == VersionMode.Explicit && !NuGetVersionHelper.IsPlausibleVersion(explicitVersion!))
            {
                var versionError = $"Invalid --template-version '{versionRaw}'. Use '{LatestVersionKeyword}', '{InstalledVersionKeyword}', or a NuGet version such as 1.2.3.";
                if (list && isJson)
                {
                    PrintListJson(null, null, versionError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDirectoryProvider.GetCurrentDirectoryInfo()).FullName, versionError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, versionError);
                }
                return ExitInvalidArgs;
            }

            // Fail fast on an explicitly supplied invalid --name before running any dotnet subprocess.
            // An option-shaped or path-bearing name (e.g. "-o", "..\Escaped") is an injection/traversal
            // risk, so reject it up front rather than after the SDK probe and pack enumeration. A name
            // derived later from --output or a prompt is validated at its own resolution point.
            if (nameFromOption is not null && !IsValidProjectName(nameFromOption))
            {
                var nameError = $"Invalid name '{nameFromOption}'. Use a simple name without path separators or invalid filename characters.";
                if (list && isJson)
                {
                    PrintListJson(null, null, nameError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, nameFromOption, (output ?? currentDirectoryProvider.GetCurrentDirectoryInfo()).FullName, nameError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, nameError);
                }
                return ExitInvalidArgs;
            }

            var currentDir = currentDirectoryProvider.GetCurrentDirectoryInfo();

            // Resolve the directory whose global.json chain governs both the template-pack SDK and the
            // scaffolded project's target framework. Project output defaults to ./<name> (whose nearest
            // existing ancestor is the current directory); an explicit --output may live under a
            // different global.json, so probe from its nearest existing ancestor — the same chain
            // `dotnet build` will later use. All dotnet subprocesses (SDK probe, pack install,
            // enumeration, scaffold) run here so the pinned TFM matches the resolved SDK, and item
            // templates surface only when this location is inside a WinUI project.
            var workingDir = NearestExistingAncestor(output ?? currentDir) ?? currentDir;

            // Prerequisite: .NET SDK. The pack install's `dotnet new install` separator depends on the
            // SDK band, and the scaffold pins the project's TFM to the SDK major, so probe it up front.
            var (sdkVersion, sdkError) = await CheckDotnetSdkAsync(workingDir, isJson, cancellationToken);
            if (sdkVersion is null)
            {
                var detail = sdkError ?? "The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                if (list && isJson)
                {
                    PrintListJson(null, null, detail);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, detail);
                }
                return ExitSdkMissing;
            }

            // Resolve and ensure the template pack (install latest / keep installed / explicit /
            // prompt-to-update), returning the version now in use. --list keeps an installed pack as-is
            // (no update prompt) but installs the latest when none is present.
            var (packOk, packVersion, packError) = await ResolveTemplatePackAsync(
                workingDir, mode, explicitVersion, useDefaults, forListing: list, sdkVersion, isJson, quiet, cancellationToken);
            if (!packOk)
            {
                var detail = packError ?? $"Failed to prepare the WinUI template pack '{TemplatePackageId}'.";
                if (list && isJson)
                {
                    PrintListJson(null, null, detail);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, detail);
                }
                return ExitTemplatePackFailed;
            }

            // Enumerate templates from the installed pack, in the working directory's context (so item
            // templates surface only when that location is inside a WinUI project).
            var templates = await EnumerateTemplatesAsync(workingDir, cancellationToken);
            if (templates.Count == 0)
            {
                var enumError = $"Could not enumerate WinUI templates from the installed pack '{TemplatePackageId}'.";
                if (list && isJson)
                {
                    PrintListJson(packVersion, null, enumError);
                }
                else if (isJson)
                {
                    PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, enumError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, enumError);
                }
                return ExitTemplatePackFailed;
            }

            // --list: print the catalog and exit without scaffolding.
            if (list)
            {
                if (isJson)
                {
                    PrintListJson(packVersion, templates, null);
                }
                else if (!quiet)
                {
                    WriteTemplateList(templates, packVersion);
                }
                return ExitSuccess;
            }

            // 1. Resolve the template entry (validate --template against the live catalog).
            WinUiTemplateEntry? entry;
            if (templateArg is not null)
            {
                entry = templates.FirstOrDefault(t => t.MatchesShortName(templateArg));
                if (entry is null)
                {
                    var valid = string.Join(", ", templates.Select(t => t.ShortName));
                    var templateError = $"Invalid template '{templateArg}'. Valid templates: {valid}. Run 'winapp new --list' to see details.";
                    if (isJson)
                    {
                        PrintJson(false, templateArg, name ?? string.Empty, (output ?? currentDir).FullName, templateError);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, templateError);
                    }
                    return ExitInvalidArgs;
                }
            }
            else if (useDefaults)
            {
                entry = templates.FirstOrDefault(t => t.MatchesShortName(DefaultTemplateShortName))
                    ?? templates.FirstOrDefault(t => t.IsProject)
                    ?? templates[0];
            }
            else
            {
                entry = await PromptTemplateAsync(templates, cancellationToken);
            }

            // 2. Resolve name. Only a genuinely absent --name enters the defaulting path; an explicitly
            // supplied but blank value (e.g. --name "   ") is kept so the validation below rejects it
            // instead of silently scaffolding the default 'WinUIApp'.
            if (name is null)
            {
                if (output is not null)
                {
                    name = output.Name;
                }
                else if (useDefaults)
                {
                    name = "WinUIApp";
                }
                else
                {
                    name = await PromptNameAsync(cancellationToken);
                }
            }

            // 2a. Validate the resolved name is a safe single path segment before it is used to build
            // the default output path or passed to dotnet new. This fails fast (exit code 2) on names
            // that would escape the current directory (e.g. "..\Escaped") or are otherwise invalid.
            if (!IsValidProjectName(name))
            {
                var nameError = $"Invalid name '{name}'. Use a simple name without path separators or invalid filename characters.";
                if (isJson)
                {
                    PrintJson(false, entry.ShortName, name ?? string.Empty, (output ?? currentDir).FullName, nameError);
                }
                else
                {
                    logger.LogError("{Error} {Detail}", UiSymbols.Error, nameError);
                }
                return ExitInvalidArgs;
            }

            // 3. Resolve the output directory. Project templates default to ./<name>; item templates are
            // added into the existing project, so they default to the current directory.
            var outputDir = output ?? (entry.IsItem
                ? currentDir
                : new DirectoryInfo(Path.Join(currentDir.FullName, name!)));

            // 3a. Preflight a non-empty output directory for project templates only (item templates are
            // intentionally created into an existing, non-empty project). dotnet new --force only
            // overwrites files the template conflicts on; it never refuses a merely non-empty directory,
            // so honour the documented --force contract here.
            if (!force && entry.IsProject)
            {
                bool outputIsNonEmpty;
                try
                {
                    outputIsNonEmpty = outputDir.Exists && outputDir.EnumerateFileSystemInfos().Any();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Enumerating a caller-selected directory can fail (e.g. a protected or locked
                    // folder). Surface it as the structured failure/exit code instead of letting it
                    // escape to Program's generic handler, which would print plain stderr and break
                    // the --json contract.
                    var accessError = $"Could not inspect output directory '{outputDir.FullName}': {ex.Message}";
                    if (isJson)
                    {
                        PrintJson(false, entry.ShortName, name!, outputDir.FullName, accessError);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, accessError);
                    }
                    return ExitInvalidArgs;
                }

                if (outputIsNonEmpty)
                {
                    var dirError = $"Output directory '{outputDir.FullName}' is not empty. Use --force to scaffold into it anyway.";
                    if (isJson)
                    {
                        PrintJson(false, entry.ShortName, name!, outputDir.FullName, dirError);
                    }
                    else
                    {
                        logger.LogError("{Error} {Detail}", UiSymbols.Error, dirError);
                    }
                    return ExitInvalidArgs;
                }
            }

            // 4. Scaffold via dotnet new from the working directory (whose global.json chain governs the
            // SDK the scaffold and later `dotnet build` resolve), pinning the TFM to that SDK.
            if (!useDefaults && !isJson && !quiet)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Creating [green]{name}[/] from [blue]{entry.ShortName}[/]...");
            }

            // Pass each token via ArgumentList (injection-safe) so a crafted --name or --output cannot
            // inject additional dotnet new options.
            var args = new List<string> { "new", entry.ShortName, "-n", name!, "-o", outputDir.FullName };

            // Pin the target framework to the resolved SDK for project templates. The WinUI project
            // templates default to net10.0, so without this an accepted .NET 8/9 SDK would scaffold a
            // project it cannot build. Item templates don't accept --dotnet-version. For a newer SDK,
            // omit the flag and let the template auto-detect the framework from the running dotnet CLI.
            if (entry.IsProject && sdkVersion.Major is >= 8 and <= 10)
            {
                args.Add("--dotnet-version");
                args.Add($"net{sdkVersion.Major}.0");
            }

            if (force)
            {
                args.Add("--force");
            }

            var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken: cancellationToken);
            if (exitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                if (isJson)
                {
                    PrintJson(false, entry.ShortName, name!, outputDir.FullName,
                        $"dotnet new failed (exit code {exitCode}): {detail}");
                }
                else
                {
                    logger.LogError("{Error} Failed to scaffold WinUI {Kind}: {Detail}", UiSymbols.Error, ProjectKind(entry), detail);
                }
                return ExitScaffoldFailed;
            }

            if (isJson)
            {
                PrintJson(true, entry.ShortName, name!, outputDir.FullName, null, packVersion);
            }
            else if (!quiet)
            {
                WriteSuccess(entry, name!, outputDir, currentDir);
            }

            return ExitSuccess;
        }

        /// <summary>Classifies the raw <c>--template-version</c> value into a mode plus explicit version.</summary>
        private static (VersionMode Mode, string? ExplicitVersion) ClassifyVersion(string? raw)
        {
            if (raw is null)
            {
                return (VersionMode.Default, null);
            }

            var trimmed = raw.Trim();
            if (trimmed.Equals(LatestVersionKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return (VersionMode.Latest, null);
            }

            if (trimmed.Equals(InstalledVersionKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return (VersionMode.Installed, null);
            }

            return (VersionMode.Explicit, trimmed);
        }

        /// <summary>
        /// Resolves and installs the template pack according to <paramref name="mode"/>, returning the
        /// version now in use. See <see cref="VersionMode"/> for the per-mode behaviour. In
        /// non-interactive contexts (<paramref name="useDefaults"/>) or when only listing
        /// (<paramref name="forListing"/>) a stale installed pack is kept rather than prompting.
        /// </summary>
        private async Task<(bool Ok, string? Version, string? Error)> ResolveTemplatePackAsync(
            DirectoryInfo cwd, VersionMode mode, string? explicitVersion, bool useDefaults, bool forListing,
            Version sdkVersion, bool isJson, bool quiet, CancellationToken cancellationToken)
        {
            var installed = await QueryInstalledPackVersionAsync(cwd, cancellationToken);

            switch (mode)
            {
                case VersionMode.Installed:
                    if (installed is null)
                    {
                        return (false, null,
                            $"No WinUI template pack is installed to use with '--template-version {InstalledVersionKeyword}'. Re-run without it (or with '--template-version {LatestVersionKeyword}') to install the latest pack.");
                    }
                    return (true, installed, null);

                case VersionMode.Explicit:
                {
                    if (installed is not null)
                    {
                        var cmp = NuGetVersionHelper.Compare(installed, explicitVersion!);
                        if (cmp is int c && c >= 0)
                        {
                            // Installed version is equal to or newer than requested: reuse it rather than
                            // reinstalling (equal) or downgrading the shared global pack (newer).
                            if (c > 0 && !isJson && !quiet)
                            {
                                logger.LogInformation(
                                    "{Info}  Using already-installed WinUI template pack {Pack} version {Installed} (newer than requested {Requested}).",
                                    UiSymbols.Info, TemplatePackageId, installed, explicitVersion);
                            }
                            return (true, installed, null);
                        }
                    }

                    var (ok, err) = await InstallPackAsync(cwd, explicitVersion, sdkVersion, isJson, quiet, cancellationToken);
                    return ok ? (true, explicitVersion, null) : (false, null, err);
                }

                case VersionMode.Latest:
                {
                    var (ok, err) = await InstallPackAsync(cwd, version: null, sdkVersion, isJson, quiet, cancellationToken);
                    if (!ok)
                    {
                        return (false, null, err);
                    }
                    return (true, await QueryInstalledPackVersionAsync(cwd, cancellationToken) ?? installed, null);
                }

                case VersionMode.Default:
                default:
                {
                    if (installed is null)
                    {
                        // Nothing installed: always grab the latest.
                        var (ok, err) = await InstallPackAsync(cwd, version: null, sdkVersion, isJson, quiet, cancellationToken);
                        if (!ok)
                        {
                            return (false, null, err);
                        }
                        return (true, await QueryInstalledPackVersionAsync(cwd, cancellationToken), null);
                    }

                    // Installed: only offer an update when the pack is actually behind the feed.
                    var latest = await GetLatestAvailableVersionAsync(cwd, installed, cancellationToken);
                    var isStale = latest is not null
                        && NuGetVersionHelper.Compare(installed, latest) is int cmp && cmp < 0;

                    if (!isStale)
                    {
                        return (true, installed, null);
                    }

                    // In non-interactive contexts (or when merely listing) keep the installed pack:
                    // updating it is a global, machine-wide side effect the caller didn't opt into.
                    if (useDefaults || forListing)
                    {
                        return (true, installed, null);
                    }

                    var update = await PromptUpdateAsync(installed!, latest!, cancellationToken);
                    if (!update)
                    {
                        return (true, installed, null);
                    }

                    var (updated, updateErr) = await InstallPackAsync(cwd, latest, sdkVersion, isJson, quiet, cancellationToken);
                    return updated ? (true, latest, null) : (false, null, updateErr);
                }
            }
        }

        /// <summary>Returns the installed WinUI pack version, or <c>null</c> when the pack isn't installed.</summary>
        private async Task<string?> QueryInstalledPackVersionAsync(DirectoryInfo cwd, CancellationToken cancellationToken)
        {
            var (exit, output, _) = await dotNetService.RunDotnetCommandAsync(cwd, ListTemplatePacksArgs, EnglishUiEnvironment, cancellationToken);
            return exit == 0 && TryGetInstalledPackVersion(output, out var version) ? version : null;
        }

        /// <summary>
        /// Returns the newest available WinUI pack version reported by <c>dotnet new update
        /// --check-only</c> (which resolves through the caller's configured NuGet feeds and surfaces
        /// prerelease updates), or <c>null</c> when the pack is already up-to-date or the check fails.
        /// Falls back to <paramref name="installed"/>'s value semantics via a null return.
        /// </summary>
        private async Task<string?> GetLatestAvailableVersionAsync(DirectoryInfo cwd, string installed, CancellationToken cancellationToken)
        {
            var (exit, output, _) = await dotNetService.RunDotnetCommandAsync(cwd, UpdateCheckArgs, EnglishUiEnvironment, cancellationToken);
            if (exit != 0)
            {
                return null;
            }

            var (_, latest) = WinUiTemplateCatalog.ParseUpdateCheck(output, TemplatePackageId);
            return latest;
        }

        /// <summary>
        /// Installs the WinUI template pack. A <c>null</c> <paramref name="version"/> installs the latest
        /// published version (floating to newest stable-or-prerelease); an explicit version pins it. The
        /// <c>dotnet new install</c> separator is chosen from the detected SDK band.
        /// </summary>
        private async Task<(bool Ok, string? Error)> InstallPackAsync(
            DirectoryInfo cwd, string? version, Version sdkVersion, bool isJson, bool quiet, CancellationToken cancellationToken)
        {
            if (!isJson && !quiet)
            {
                logger.LogInformation("{Info}  Installing WinUI template pack {Pack}...", UiSymbols.Info, TemplatePackageId);
            }

            string packageArg;
            if (version is null)
            {
                packageArg = TemplatePackageId;
            }
            else
            {
                var separator = sdkVersion >= FirstAtSeparatorSdkVersion ? "@" : "::";
                packageArg = $"{TemplatePackageId}{separator}{version}";
            }

            var (installExit, installOut, installErr) = await dotNetService.RunDotnetCommandAsync(
                cwd, new[] { "new", "install", packageArg }, cancellationToken: cancellationToken);

            if (installExit != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(installErr) ? installErr.Trim() : installOut.Trim();
                if (!isJson)
                {
                    logger.LogError("{Error} Failed to install the WinUI template pack: {Detail}", UiSymbols.Error, detail);
                }

                var error = $"Failed to install the WinUI template pack '{packageArg}' (dotnet new install exit code {installExit})";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    error += $": {detail}";
                }

                return (false, error);
            }

            return (true, null);
        }

        /// <summary>Runs <c>dotnet new list</c> in <paramref name="contextDir"/> and parses the WinUI templates.</summary>
        private async Task<IReadOnlyList<WinUiTemplateEntry>> EnumerateTemplatesAsync(DirectoryInfo contextDir, CancellationToken cancellationToken)
        {
            var (exit, output, _) = await dotNetService.RunDotnetCommandAsync(contextDir, ListTemplatesArgs, EnglishUiEnvironment, cancellationToken);
            return exit == 0 ? WinUiTemplateCatalog.ParseList(output) : [];
        }

        private async Task<WinUiTemplateEntry> PromptTemplateAsync(IReadOnlyList<WinUiTemplateEntry> templates, CancellationToken cancellationToken)
        {
            var labels = templates.Select(FormatChoice).ToList();

            var prompt = new SelectionPrompt<string>()
                .Title("Which WinUI template would you like to use?")
                .AddChoices(labels);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            ansiConsole.MarkupLineInterpolated($"Which WinUI template would you like to use? [underline]{selected}[/]");
            var index = labels.IndexOf(selected);
            return templates[index];
        }

        /// <summary>Human-friendly choice label combining the display name and canonical short name.</summary>
        private static string FormatChoice(WinUiTemplateEntry entry)
        {
            var name = string.IsNullOrEmpty(entry.DisplayName) ? entry.ShortName : entry.DisplayName;
            return entry.IsItem ? $"{name} (item — {entry.ShortName})" : $"{name} ({entry.ShortName})";
        }

        private async Task<string> PromptNameAsync(CancellationToken cancellationToken)
        {
            // Reuse the same validation the handler applies so invalid interactive input (empty,
            // path separators, "..", reserved device names, etc.) is corrected in place instead of
            // being accepted here and then rejected after the wizard completes.
            var prompt = new TextPrompt<string>("What should the app be named?")
                .DefaultValue("WinUIApp")
                .Validate(value => IsValidProjectName(value)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Use a simple name without path separators or invalid filename characters."));

            return await ansiConsole.PromptAsync(prompt, cancellationToken);
        }

        /// <summary>
        /// Prompts whether to update a stale template pack. Deliberately has <b>no default</b>: updating
        /// the pack is a global, machine-wide side effect, so the caller must answer explicitly.
        /// </summary>
        private async Task<bool> PromptUpdateAsync(string installed, string latest, CancellationToken cancellationToken)
        {
            var prompt = new TextPrompt<bool>($"A newer WinUI template pack is available ({installed} \u2192 {latest}). Update?")
                .AddChoice(true)
                .AddChoice(false)
                .WithConverter(value => value ? "y" : "n");

            return await ansiConsole.PromptAsync(prompt, cancellationToken);
        }

        /// <summary>Writes the success message and a template-specific next step.</summary>
        private void WriteSuccess(WinUiTemplateEntry entry, string name, DirectoryInfo outputDir, DirectoryInfo currentDir)
        {
            if (entry.IsItem)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Added [green]{name}[/] to the project in [blue]{outputDir.FullName}[/].");
                return;
            }

            var relative = Path.GetRelativePath(currentDir.FullName, outputDir.FullName);
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Created WinUI {ProjectKind(entry)} [green]{name}[/] at [blue]{outputDir.FullName}[/].");

            if (TagsContain(entry.Tags, "Library"))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: from your app project, run [blue]dotnet add reference \"{Path.Join(outputDir.FullName, name + ".csproj")}\"[/].");
            }
            else if (TagsContain(entry.Tags, "Test"))
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: [blue]cd \"{relative}\"[/] then [blue]dotnet run[/] — this packaged MSTest app runs its tests when launched (not via [blue]dotnet test[/]).");
            }
            else
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: [blue]cd \"{relative}\"[/] then [blue]dotnet run[/].");
            }
        }

        /// <summary>Writes the human-readable template list for <c>--list</c>.</summary>
        private void WriteTemplateList(IReadOnlyList<WinUiTemplateEntry> templates, string? packVersion)
        {
            var versionSuffix = string.IsNullOrEmpty(packVersion) ? string.Empty : $" (pack {packVersion})";
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Available WinUI templates{versionSuffix}:");

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Template");
            table.AddColumn("Short name");
            table.AddColumn("Type");

            foreach (var entry in templates)
            {
                table.AddRow(
                    Markup.Escape(string.IsNullOrEmpty(entry.DisplayName) ? entry.ShortName : entry.DisplayName),
                    Markup.Escape(entry.ShortName),
                    Markup.Escape(entry.Type));
            }

            ansiConsole.Write(table);
        }

        /// <summary>
        /// Walks up from <paramref name="dir"/> to the nearest directory that already exists on disk, so
        /// commands whose <c>global.json</c> resolution must match the eventual project can be run from a
        /// real working directory even when the output directory hasn't been created yet. Returns
        /// <c>null</c> only if no ancestor exists (e.g. a drive that isn't mounted).
        /// </summary>
        private static DirectoryInfo? NearestExistingAncestor(DirectoryInfo dir)
        {
            for (var d = dir; d is not null; d = d.Parent)
            {
                if (d.Exists)
                {
                    return d;
                }
            }

            return null;
        }

        /// <summary>True when any '/'-separated segment of <paramref name="tags"/> equals <paramref name="segment"/>.</summary>
        private static bool TagsContain(string tags, string segment)
            => tags.Split('/').Any(s => s.Trim().Equals(segment, StringComparison.OrdinalIgnoreCase));

        /// <summary>Human-facing noun for the created artifact, derived from the template's tags.</summary>
        private static string ProjectKind(WinUiTemplateEntry entry)
        {
            if (entry.IsItem)
            {
                return "item";
            }

            if (TagsContain(entry.Tags, "Library"))
            {
                return "class library";
            }

            if (TagsContain(entry.Tags, "Test"))
            {
                return "unit test project";
            }

            return "app";
        }

        /// <summary>
        /// Verifies the .NET SDK is installed and meets the minimum version. Returns the detected SDK
        /// version, or <c>null</c> (without installing anything) with a specific failure reason if the
        /// SDK is missing, too old, or its version can't be determined. Runs <c>dotnet --version</c> in
        /// <paramref name="probeDir"/> so <c>global.json</c> is resolved from the project's own
        /// directory chain rather than the caller's working directory.
        /// </summary>
        private async Task<(Version? Version, string? Error)> CheckDotnetSdkAsync(DirectoryInfo probeDir, bool isJson, CancellationToken cancellationToken)
        {
            int exitCode;
            string stdout;
            try
            {
                (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(probeDir, VersionArgs, cancellationToken: cancellationToken);
            }
            catch (Win32Exception)
            {
                // dotnet executable not found on PATH.
                exitCode = -1;
                stdout = string.Empty;
            }

            if (exitCode != 0)
            {
                const string missing = "The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                if (!isJson)
                {
                    logger.LogError("{Error} {Message}", UiSymbols.Error, missing);
                }
                return (null, missing);
            }

            var versionText = stdout.Trim();
            // Strip any prerelease suffix (e.g. 9.0.100-preview.1) before parsing.
            var dashIndex = versionText.IndexOf('-');
            if (dashIndex > 0)
            {
                versionText = versionText[..dashIndex];
            }

            if (Version.TryParse(versionText, out var installed))
            {
                if (installed < MinimumSdkVersion)
                {
                    var tooOld = $".NET SDK {installed} is installed, but {MinimumSdkVersion} or newer is required. Update from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
                    if (!isJson)
                    {
                        logger.LogError("{Error} {Message}", UiSymbols.Error, tooOld);
                    }
                    return (null, tooOld);
                }

                return (installed, null);
            }

            // Unparseable output means we can't confirm a usable SDK — fail the prerequisite rather
            // than optimistically proceeding into a confusing `dotnet new` error.
            var unparseable = $"Could not determine the installed .NET SDK version (got '{stdout.Trim()}'). Ensure the .NET SDK {MinimumSdkVersion} or newer is installed from https://dotnet.microsoft.com/download, then re-run 'winapp new'.";
            if (!isJson)
            {
                logger.LogError("{Error} {Message}", UiSymbols.Error, unparseable);
            }
            return (null, unparseable);
        }

        private void PrintJson(bool created, string? templateShortName, string name, string path, string? error, string? templateVersion = null)
        {
            var result = new NewCommandResult
            {
                Created = created,
                Template = templateShortName,
                Name = name,
                Path = path,
                TemplateVersion = templateVersion,
                Error = error
            };

            var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewCommandResult);
            // Write via the raw output writer (not ansiConsole.WriteLine) so Spectre does not
            // word-wrap the JSON to the console width and corrupt it on narrow terminals.
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }

        private void PrintListJson(string? templateVersion, IReadOnlyList<WinUiTemplateEntry>? templates, string? error)
        {
            var result = new NewListResult
            {
                Listed = error is null,
                TemplateVersion = templateVersion,
                Templates = templates?.Select(t => new NewTemplateInfo
                {
                    ShortName = t.ShortName,
                    Aliases = t.ShortNames.ToArray(),
                    DisplayName = t.DisplayName,
                    Type = t.Type,
                    Tags = t.Tags,
                }).ToList(),
                Error = error,
            };

            var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewListResult);
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }
    }
}

internal sealed class NewCommandResult
{
    public bool Created { get; set; }
    public string? Template { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? TemplateVersion { get; set; }
    public string? Error { get; set; }
}

internal sealed class NewTemplateInfo
{
    public string? ShortName { get; set; }
    public string[]? Aliases { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? Tags { get; set; }
}

internal sealed class NewListResult
{
    public bool Listed { get; set; }
    public string? TemplateVersion { get; set; }
    public List<NewTemplateInfo>? Templates { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(NewCommandResult))]
[JsonSerializable(typeof(NewListResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class NewCommandJsonContext : JsonSerializerContext;
