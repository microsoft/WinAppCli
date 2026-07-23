// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// The WinUI template to scaffold. Each value maps to a friendly CLI alias and an official
/// <c>dotnet new</c> short name from the WindowsAppSDK WinUI template pack. Parsed case-insensitively,
/// so <c>--template navview</c> resolves to <see cref="NavView"/>.
/// </summary>
internal enum WinUiTemplate
{
    Blank,
    NavView,
    TabView,
    Mvvm,
    Lib,
    UnitTest
}

internal class NewCommand : Command, IShortDescription
{
    public string ShortDescription => "Create a new WinUI app";

    /// <summary>NuGet package id of the official WASDK WinUI C# template pack.</summary>
    internal const string TemplatePackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";

    /// <summary>
    /// Pinned template-pack version (the pack is preview + externally owned; pinning avoids silent
    /// breakage). Override with <c>--template-version</c>.
    /// </summary>
    internal const string DefaultTemplateVersion = "0.0.6-alpha";

    /// <summary>Minimum .NET SDK version, matching <c>winapp run</c>'s project mode.</summary>
    internal static readonly Version MinimumSdkVersion = new(8, 0, 100);

    // Fixed dotnet argument lists, hoisted to static readonly fields to satisfy CA1861.
    private static readonly string[] VersionArgs = ["--version"];
    private static readonly string[] ListTemplatePacksArgs = ["new", "uninstall"];

    // Distinct exit codes so agents can branch on the failing stage without parsing text.
    internal const int ExitSuccess = 0;
    internal const int ExitInvalidArgs = 2;
    internal const int ExitSdkMissing = 3;
    internal const int ExitTemplatePackFailed = 4;
    internal const int ExitScaffoldFailed = 5;

    public static Option<WinUiTemplate?> TemplateOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<DirectoryInfo?> OutputOption { get; }
    public static Option<bool> UseDefaultsOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<string?> TemplateVersionOption { get; }

    static NewCommand()
    {
        TemplateOption = new Option<WinUiTemplate?>("--template", "-t")
        {
            Description = "WinUI template: blank (default), navview, tabview, mvvm, lib, or unittest.",
            HelpName = "blank|navview|tabview|mvvm|lib|unittest"
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
            Description = "Do not prompt; use defaults (blank template, name from --output/--name)."
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Scaffold even if the output directory already contains files."
        };
        TemplateVersionOption = new Option<string?>("--template-version")
        {
            Description = $"Version of the WinUI template pack to use (default: {DefaultTemplateVersion})."
        };
    }

    public NewCommand() : base("new", "Create a new WinUI app from an official Windows App SDK template. Interactive by default: pick a template (blank, navigation view, tab view, MVVM, class library, or unit test), then a name (the output directory defaults to ./<name>). Automatically uses defaults in non-interactive environments (use --use-defaults to skip prompts explicitly). Requires the .NET SDK; installs the WinUI template pack on demand and delegates scaffolding to 'dotnet new'. Scaffolds against the installed SDK's target framework and prints a template-specific next step when done (e.g. 'winapp run' for app templates).")
    {
        Options.Add(TemplateOption);
        Options.Add(NameOption);
        Options.Add(OutputOption);
        Options.Add(UseDefaultsOption);
        Options.Add(ForceOption);
        Options.Add(TemplateVersionOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Friendly label and official <c>dotnet new</c> short name for a template.</summary>
    internal static (string ShortName, string Label) TemplateInfo(WinUiTemplate template) => template switch
    {
        WinUiTemplate.Blank => ("winui", "Blank app"),
        WinUiTemplate.NavView => ("winui-navview", "Navigation view app"),
        WinUiTemplate.TabView => ("winui-tabview", "Tab view app"),
        WinUiTemplate.Mvvm => ("winui-mvvm", "MVVM app"),
        WinUiTemplate.Lib => ("winui-lib", "Class library"),
        WinUiTemplate.UnitTest => ("winui-unittest", "Unit test project"),
        _ => ("winui", "Blank app")
    };

    /// <summary>Human-facing noun for the created artifact, used in the success message.</summary>
    internal static string ProjectKind(WinUiTemplate template) => template switch
    {
        WinUiTemplate.Lib => "class library",
        WinUiTemplate.UnitTest => "unit test project",
        _ => "app"
    };

    /// <summary>
    /// A plausible NuGet package version: non-empty, starts with a digit, and contains only
    /// version characters. Rejects whitespace/quote-laden input (defense-in-depth against argument
    /// injection, and a fast-fail for typos).
    /// </summary>
    internal static bool IsPlausibleVersion(string version) =>
        !string.IsNullOrWhiteSpace(version)
        && char.IsDigit(version[0])
        && version.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>
    /// Returns true only when <paramref name="name"/> is a safe single path segment. The resolved
    /// name becomes both the default output directory (<c>./&lt;name&gt;</c>) and the <c>dotnet new</c>
    /// project name, so path separators, <c>.</c>/<c>..</c>, rooted paths, invalid filename
    /// characters, Windows reserved device names, and trailing dot/space names are rejected so the
    /// scaffold can't escape the current directory or produce an unusable folder.
    /// </summary>
    internal static bool IsValidProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
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
    /// requested one — otherwise an explicit <c>--template-version</c> would silently be ignored.
    /// </summary>
    internal static bool IsTemplatePackInstalled(string uninstallListOutput, string requestedVersion)
    {
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
                    var installedVersion = line[(idx + marker.Length)..].Trim();
                    return installedVersion.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase);
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
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var template = parseResult.GetValue(TemplateOption);
            var nameFromOption = parseResult.GetValue(NameOption);
            var output = parseResult.GetValue(OutputOption);
            var useDefaults = parseResult.GetValue(UseDefaultsOption);
            var force = parseResult.GetValue(ForceOption);
            var templateVersion = parseResult.GetValue(TemplateVersionOption) ?? DefaultTemplateVersion;
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);

            var name = nameFromOption;

            // Non-interactive environments (CI, piped stdin) behave like --use-defaults to avoid
            // prompt exceptions — mirrors InitCommand.
            if (!useDefaults && !ansiConsole.Profile.Capabilities.Interactive)
            {
                if (!isJson)
                {
                    logger.LogWarning("{Warning}  Non-interactive environment detected. Using default values.", UiSymbols.Warning);
                }
                useDefaults = true;
            }

            // 1. Resolve template
            if (template is null)
            {
                template = useDefaults ? WinUiTemplate.Blank : await PromptTemplateAsync(cancellationToken);
            }

            // 2. Resolve name
            if (string.IsNullOrWhiteSpace(name))
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

            // 3. Resolve output directory (default ./<name>)
            var currentDir = currentDirectoryProvider.GetCurrentDirectoryInfo();

            // 3a. Validate the resolved name is a safe single path segment before it is used to build
            // the default output path or passed to dotnet new. This fails fast (exit code 2) on names
            // that would escape the current directory (e.g. "..\Escaped") or are otherwise invalid.
            if (!IsValidProjectName(name))
            {
                if (isJson)
                {
                    PrintJson(false, template.Value, name ?? string.Empty, (output ?? currentDir).FullName,
                        $"Invalid name '{name}'. Use a simple name without path separators or invalid filename characters.");
                }
                else
                {
                    logger.LogError("{Error} Invalid name '{Name}'. Use a simple name without path separators or invalid filename characters.",
                        UiSymbols.Error, name);
                }
                return ExitInvalidArgs;
            }

            var outputDir = output ?? new DirectoryInfo(Path.Join(currentDir.FullName, name!));

            // 3b. Validate the template-pack version (a NuGet version starts with a digit and contains
            // only version characters). This fails fast on clearly invalid input with exit code 2.
            if (!IsPlausibleVersion(templateVersion))
            {
                if (isJson)
                {
                    PrintJson(false, template.Value, name!, outputDir.FullName,
                        $"Invalid --template-version '{templateVersion}'. Expected a NuGet version such as {DefaultTemplateVersion}.");
                }
                else
                {
                    logger.LogError("{Error} Invalid --template-version '{Version}'. Expected a NuGet version such as {Default}.",
                        UiSymbols.Error, templateVersion, DefaultTemplateVersion);
                }
                return ExitInvalidArgs;
            }

            // 4. Prerequisite: .NET SDK (fail fast, do not install anything)
            var sdkVersion = await CheckDotnetSdkAsync(isJson, cancellationToken);
            if (sdkVersion is null)
            {
                if (isJson)
                {
                    PrintJson(false, template.Value, name!, outputDir.FullName,
                        "The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.");
                }
                return ExitSdkMissing;
            }

            // 5. Ensure the WinUI template pack is installed (idempotent)
            var packOk = await EnsureTemplatePackAsync(currentDir, templateVersion, isJson, cancellationToken);
            if (!packOk)
            {
                if (isJson)
                {
                    PrintJson(false, template.Value, name!, outputDir.FullName,
                        $"Failed to install the WinUI template pack '{TemplatePackageId}::{templateVersion}'.");
                }
                return ExitTemplatePackFailed;
            }

            // 6. Scaffold via dotnet new
            var (shortName, _) = TemplateInfo(template.Value);
            if (!useDefaults && !isJson && !quiet)
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Creating [green]{name}[/] from [blue]{shortName}[/]...");
            }

            // Pass each token via ArgumentList (injection-safe) so a crafted --name or --output cannot
            // inject additional dotnet new options.
            var args = new List<string> { "new", shortName, "-n", name!, "-o", outputDir.FullName };

            // Pin the target framework to the installed SDK. The WinUI templates default to net10.0, so
            // without this an accepted .NET 8/9 SDK would scaffold a project it cannot build. The
            // templates only offer net8.0/net9.0/net10.0; for a newer SDK, omit the flag and let the
            // template auto-detect the framework from the running dotnet CLI.
            if (sdkVersion.Major is >= 8 and <= 10)
            {
                args.Add("--dotnet-version");
                args.Add($"net{sdkVersion.Major}.0");
            }

            if (force)
            {
                args.Add("--force");
            }

            var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(currentDir, args, cancellationToken);
            if (exitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                if (isJson)
                {
                    PrintJson(false, template.Value, name!, outputDir.FullName,
                        $"dotnet new failed (exit code {exitCode}): {detail}");
                }
                else
                {
                    logger.LogError("{Error} Failed to scaffold WinUI app: {Detail}", UiSymbols.Error, detail);
                }
                return ExitScaffoldFailed;
            }

            if (isJson)
            {
                PrintJson(true, template.Value, name!, outputDir.FullName, null);
            }
            else if (!quiet)
            {
                var relative = Path.GetRelativePath(currentDir.FullName, outputDir.FullName);
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Created WinUI {ProjectKind(template.Value)} [green]{name}[/] at [blue]{outputDir.FullName}[/].");
                switch (template.Value)
                {
                    case WinUiTemplate.Lib:
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: from your app project, run [blue]dotnet add reference \"{Path.Join(relative, Path.GetFileName(name!) + ".csproj")}\"[/].");
                        break;
                    case WinUiTemplate.UnitTest:
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: [blue]cd \"{relative}\"[/] then [blue]winapp run .[/] — this packaged MSTest app runs its tests when launched (not via [blue]dotnet test[/]).");
                        break;
                    default:
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Info}  Next: [blue]cd \"{relative}\"[/] then [blue]winapp run .[/]");
                        break;
                }
            }

            return ExitSuccess;
        }

        private async Task<WinUiTemplate> PromptTemplateAsync(CancellationToken cancellationToken)
        {
            var templates = Enum.GetValues<WinUiTemplate>();
            var labels = templates.Select(t => TemplateInfo(t).Label).ToList();

            var prompt = new SelectionPrompt<string>()
                .Title("Which WinUI app would you like to create?")
                .AddChoices(labels);

            var selected = await ansiConsole.PromptAsync(prompt, cancellationToken);
            ansiConsole.MarkupLineInterpolated($"Which WinUI app would you like to create? [underline]{selected}[/]");
            var index = labels.IndexOf(selected);
            return templates[index];
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
        /// Verifies the .NET SDK is installed and meets the minimum version. Returns the detected SDK
        /// version, or <c>null</c> (without installing anything) with an actionable message if the SDK
        /// is missing, too old, or its version can't be determined. The caller uses the returned major
        /// version to pin the scaffolded project's target framework.
        /// </summary>
        private async Task<Version?> CheckDotnetSdkAsync(bool isJson, CancellationToken cancellationToken)
        {
            var cwd = currentDirectoryProvider.GetCurrentDirectoryInfo();
            int exitCode;
            string stdout;
            try
            {
                (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(cwd, VersionArgs, cancellationToken);
            }
            catch (Win32Exception)
            {
                // dotnet executable not found on PATH.
                exitCode = -1;
                stdout = string.Empty;
            }

            if (exitCode != 0)
            {
                if (!isJson)
                {
                    logger.LogError("{Error} The .NET SDK is required to create a WinUI app. Install it from https://dotnet.microsoft.com/download, then re-run 'winapp new'.", UiSymbols.Error);
                }
                return null;
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
                    if (!isJson)
                    {
                        logger.LogError("{Error} .NET SDK {Installed} is installed, but {Minimum} or newer is required. Update from https://dotnet.microsoft.com/download, then re-run 'winapp new'.",
                            UiSymbols.Error, installed, MinimumSdkVersion);
                    }
                    return null;
                }

                return installed;
            }

            // Unparseable output means we can't confirm a usable SDK — fail the prerequisite rather
            // than optimistically proceeding into a confusing `dotnet new` error.
            if (!isJson)
            {
                logger.LogError("{Error} Could not determine the installed .NET SDK version (got '{Output}'). Ensure the .NET SDK {Minimum} or newer is installed from https://dotnet.microsoft.com/download, then re-run 'winapp new'.",
                    UiSymbols.Error, stdout.Trim(), MinimumSdkVersion);
            }
            return null;
        }

        /// <summary>
        /// Ensures the WinUI template pack is installed. Checks the currently installed packs first so
        /// repeated runs don't hit the network; installs the pinned version only when missing.
        /// </summary>
        private async Task<bool> EnsureTemplatePackAsync(DirectoryInfo cwd, string version, bool isJson, CancellationToken cancellationToken)
        {
            // `dotnet new uninstall` with no args lists installed template packages (and versions).
            var (listExit, listOut, _) = await dotNetService.RunDotnetCommandAsync(cwd, ListTemplatePacksArgs, cancellationToken);
            if (listExit == 0 && IsTemplatePackInstalled(listOut, version))
            {
                return true;
            }

            if (!isJson)
            {
                logger.LogInformation("{Info}  Installing WinUI template pack {Pack}...", UiSymbols.Info, TemplatePackageId);
            }

            var (installExit, installOut, installErr) = await dotNetService.RunDotnetCommandAsync(
                cwd, new[] { "new", "install", $"{TemplatePackageId}::{version}" }, cancellationToken);

            if (installExit != 0)
            {
                if (!isJson)
                {
                    var detail = !string.IsNullOrWhiteSpace(installErr) ? installErr.Trim() : installOut.Trim();
                    logger.LogError("{Error} Failed to install the WinUI template pack: {Detail}", UiSymbols.Error, detail);
                }
                return false;
            }

            return true;
        }

        private void PrintJson(bool created, WinUiTemplate template, string name, string path, string? error)
        {
            var (shortName, _) = TemplateInfo(template);
            var result = new NewCommandResult
            {
                Created = created,
                Template = shortName,
                Name = name,
                Path = path,
                Error = error
            };

            var json = JsonSerializer.Serialize(result, NewCommandJsonContext.Default.NewCommandResult);
            // Write via the raw output writer (not ansiConsole.WriteLine) so Spectre does not
            // word-wrap the JSON to the console width and corrupt it on narrow terminals.
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
    public string? Error { get; set; }
}

[JsonSerializable(typeof(NewCommandResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class NewCommandJsonContext : JsonSerializerContext;
