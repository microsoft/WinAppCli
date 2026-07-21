// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IProjectRunService" />
internal sealed partial class ProjectRunService(
    IDotNetService dotNetService,
    IProjectDetectionService projectDetectionService,
    ICsWinRTMetadataShimService csWinRTMetadataShimService,
    IAnsiConsole ansiConsole,
    ILogger<ProjectRunService> logger) : IProjectRunService
{
    /// <summary>MSBuild properties requested from the evaluate step (always ≥2 → JSON output).</summary>
    private static readonly string[] RequestedProperties =
    [
        "TargetDir",
        "RunCommand",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        "EnableMsixTooling",
        "OutputType",
    ];

    /// <summary>Upper bound on build-output lines retained for the spinner failure dump (bounded tail).</summary>
    private const int MaxBuildTailLines = 500;

    /// <inheritdoc />
    public async Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken, string? projectSelector = null)
    {
        // Explicit file input: a .csproj (project mode) or a .sln/.slnx (solution mode).
        if (input is FileInfo file)
        {
            if (IsSolutionFile(file))
            {
                return await ResolveSolutionAsync(file, projectSelector, cancellationToken);
            }

            if (!string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectRunException(
                    $"'{file.FullName}' is not a runnable input. Pass a .csproj, a .sln/.slnx solution, a directory containing one, or a build-output folder.");
            }

            var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

            // A bare .csproj input has no solution context, so $(SolutionDir) and the sibling Solution*
            // properties are undefined — projects that reference them in imports/AdditionalFiles then
            // fail to build (e.g. a source generator reading $(SolutionDir)…\Resources.resw). Discover
            // the owning solution by walking up so the build defines them exactly as `dotnet build <sln>`
            // / Visual Studio does. Null when none is found → behavior is identical to before.
            var owningSolution = FindOwningSolution(file);
            return new RunInputResolution(WinAppRunMode.Project, file, projectDir, owningSolution);
        }

        var dir = (DirectoryInfo)input;

        // A solution in the directory wins over loose .csproj files: it carries the config→platform
        // map and defines $(SolutionDir), which some projects (e.g. those importing shared props via
        // $(SolutionDir)) need to build at all. Prefer it, matching what a developer opens in VS.
        List<FileInfo> solutions;
        try
        {
            solutions = dir.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly)
                .Concat(dir.EnumerateFiles("*.slnx", SearchOption.TopDirectoryOnly))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            solutions = [];
        }

        if (solutions.Count == 1)
        {
            return await ResolveSolutionAsync(solutions[0], projectSelector, cancellationToken);
        }

        if (solutions.Count > 1)
        {
            var slnNames = string.Join(", ", solutions.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            throw new ProjectRunException(
                $"Multiple solution files found in '{dir.FullName}' ({slnNames}). Specify which one to run, e.g. 'winapp run {solutions[0].Name}'.");
        }

        List<FileInfo> csprojs;
        try
        {
            csprojs = dir.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            csprojs = [];
        }

        // No top-level .csproj → folder mode (existing, unchanged behavior). Build-output folders
        // (bin/…) fall here. This path performs NO MSBuild evaluation, so folder mode stays identical.
        if (csprojs.Count == 0)
        {
            return new RunInputResolution(WinAppRunMode.Folder, null, dir);
        }

        if (csprojs.Count == 1)
        {
            return new RunInputResolution(WinAppRunMode.Project, csprojs[0], dir);
        }

        // A --project selector disambiguates directly without evaluation.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(csprojs, projectSelector, dir);
            if (selected is null)
            {
                var available = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single .csproj in '{dir.FullName}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, dir);
        }

        // Multiple .csproj files — classify each via MSBuild evaluation so an executable/test project
        // is detected even when OutputType/IsTestProject come from an import (SDK defaults,
        // Directory.Build.props, the test SDK) rather than inline XML. A static parse cannot see those
        // and could silently pick the wrong project (spec M5). Evaluation falls back to the static
        // parse per-project when the SDK/restore is unavailable, so behavior never regresses.
        var (dirApps, dirTests) = await ClassifyRunnablesAsync(csprojs, dir, null, cancellationToken);

        var dirPick = PickRunnableProject(dirApps, dirTests, out var dirPickedTest);
        if (dirPick is not null)
        {
            if (dirPickedTest)
            {
                LogRunningLoneTestProject(dirPick, dir.FullName);
            }

            return new RunInputResolution(WinAppRunMode.Project, dirPick, dir);
        }

        // Zero or several runnable candidates → we cannot safely guess; require explicit selection.
        var names = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new ProjectRunException(
            $"Multiple .csproj files found in '{dir.FullName}' ({names}). Specify which project to run, e.g. 'winapp run {csprojs[0].Name}' or --project <name>.");
    }

    /// <summary>True when the file is a solution (<c>.sln</c> or the newer XML <c>.slnx</c>).</summary>
    private static bool IsSolutionFile(FileInfo file) =>
        string.Equals(file.Extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file.Extension, ".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Matches any project entry in a classic <c>.sln</c>, capturing the (relative) project path (any type).</summary>
    [GeneratedRegex(
        "Project\\(\"\\{[^\"}]*\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlnAnyProjectPathRegex();

    /// <summary>
    /// Finds the solution that owns a bare <c>.csproj</c> so a direct-file run defines <c>$(SolutionDir)</c>
    /// the same way <c>dotnet build &lt;sln&gt;</c> / Visual Studio do. Walks up from the project directory;
    /// at the nearest ancestor that contains any <c>.sln</c>/<c>.slnx</c>, prefers a solution that actually
    /// lists this project, else uses it when there is exactly one, else returns null (several solutions,
    /// none demonstrably owning → don't guess). Returns null when no solution is found at all.
    /// </summary>
    private static FileInfo? FindOwningSolution(FileInfo csproj)
    {
        for (var dir = csproj.Directory; dir is not null; dir = dir.Parent)
        {
            List<FileInfo> solutions;
            try
            {
                solutions = dir.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly)
                    .Concat(dir.EnumerateFiles("*.slnx", SearchOption.TopDirectoryOnly))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (solutions.Count == 0)
            {
                continue;
            }

            // Prefer a solution at this level that actually lists the project (deterministic: alpha order).
            var owning = solutions.FirstOrDefault(s => SolutionListsProject(s, csproj));
            if (owning is not null)
            {
                return owning;
            }

            // Exactly one solution here and we couldn't confirm it lists the project (empty/unreadable) —
            // it is still the owning solution by locality. Several with no listing match → don't guess.
            return solutions.Count == 1 ? solutions[0] : null;
        }

        return null;
    }

    /// <summary>
    /// True when a solution file lists the given project. Parses the solution text directly (no
    /// <c>dotnet</c> shell-out): classic <c>.sln</c> via the project-entry regex, <c>.slnx</c> via XML.
    /// Each listed path is resolved relative to the solution directory and compared to the project's
    /// full path. Returns false when the file cannot be read or lists nothing matching.
    /// </summary>
    private static bool SolutionListsProject(FileInfo solution, FileInfo project)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        var relativePaths = string.Equals(solution.Extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? ExtractSlnxProjectPaths(text)
            : ExtractSlnProjectPaths(text);

        foreach (var relative in relativePaths)
        {
            string full;
            try
            {
                var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                full = Path.GetFullPath(Path.Combine(solutionDir, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(full, project.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts every listed project path (any type) from a classic <c>.sln</c> file.</summary>
    private static List<string> ExtractSlnAllProjectPaths(string text) =>
        SlnAnyProjectPathRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Extracts the relative <c>.csproj</c> paths listed in a classic <c>.sln</c> file.</summary>
    private static List<string> ExtractSlnProjectPaths(string text) =>
        ExtractSlnAllProjectPaths(text)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Extracts every listed project path (any type) from an XML <c>.slnx</c> solution.</summary>
    private static List<string> ExtractSlnxAllProjectPaths(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Path", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>Extracts the relative <c>.csproj</c> paths from an XML <c>.slnx</c> solution.</summary>
    private static List<string> ExtractSlnxProjectPaths(string text) =>
        ExtractSlnxAllProjectPaths(text)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Managed project types that <c>dotnet restore</c> handles on a VS-less host.</summary>
    private static readonly string[] ManagedProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    /// <summary>True when the project path is a dotnet-restorable managed type (<c>.csproj</c>/<c>.vbproj</c>/<c>.fsproj</c>).</summary>
    private static bool IsManagedProjectPath(string path) =>
        ManagedProjectExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the restore plan for a solution build. Visual Studio (and <c>dotnet build &lt;sln&gt;</c>)
    /// restore the <em>whole solution</em> before building — including projects that are build-dependencies
    /// but not <c>ProjectReference</c>s of the target (e.g. an out-of-process COM server invoked by a custom
    /// MSBuild target). <c>winapp run</c> restores only the single target, so those siblings have no
    /// <c>project.assets.json</c> and the build fails with <c>NETSDK1004</c>. This enumerates the solution's
    /// listed projects (a pure text parse — no <c>dotnet</c> shell-out, no <c>File.Exists</c> gating) and
    /// returns the managed siblings to restore, excluding the target itself.
    /// <para>
    /// <paramref name="AllManaged"/> is true when every listed project is a restorable managed type. When it
    /// is false (a native <c>.vcxproj</c>/<c>.wapproj</c>/<c>.shproj</c> is present), a single
    /// <c>dotnet restore &lt;sln&gt;</c> would error on a VS-less box, so the caller restores the managed
    /// siblings individually and skips the natives.
    /// </para>
    /// </summary>
    internal static (bool AllManaged, List<FileInfo> ManagedSiblings) ComputeSolutionRestorePlan(FileInfo solution, FileInfo target)
    {
        string text;
        try
        {
            text = File.ReadAllText(solution.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, []);
        }

        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        var projectPaths = (string.Equals(solution.Extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                ? ExtractSlnxAllProjectPaths(text)
                : ExtractSlnAllProjectPaths(text))
            // Drop classic-.sln solution-folder entries (their "path" is the folder name, no ...proj extension).
            .Where(p => p.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allManaged = projectPaths.All(IsManagedProjectPath);

        var siblings = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in projectPaths.Where(IsManagedProjectPath))
        {
            string full;
            try
            {
                var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                full = Path.GetFullPath(Path.Combine(solutionDir, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(full, target.FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(full))
            {
                siblings.Add(new FileInfo(full));
            }
        }

        return (allManaged, siblings);
    }

    /// <summary>
    /// Resolves the runnable app project out of a solution and records the solution on the result so
    /// the build defines <c>$(SolutionDir)</c>. The solution's project list comes from
    /// <c>dotnet sln &lt;sln&gt; list</c>; each candidate is classified with the same MSBuild
    /// evaluation used for a multi-<c>.csproj</c> directory. Exactly one launchable (non-test
    /// executable) project is required unless a matching <c>--project</c> selector is supplied.
    /// </summary>
    private async Task<RunInputResolution> ResolveSolutionAsync(FileInfo solution, string? projectSelector, CancellationToken cancellationToken)
    {
        var solutionDir = solution.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        var projects = await GetSolutionProjectsAsync(solution, solutionDir, cancellationToken);

        if (projects.Count == 0)
        {
            throw new ProjectRunException(
                $"No .csproj projects were found in '{solution.Name}'. 'winapp run' needs a runnable C# project in the solution.");
        }

        // An explicit --project selector short-circuits classification.
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selected = MatchProjectSelector(projects, projectSelector, solutionDir);
            if (selected is null)
            {
                var available = string.Join(", ", projects.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match a single project in '{solution.Name}'. Available: {available}.");
            }

            return new RunInputResolution(WinAppRunMode.Project, selected, selected.Directory ?? solutionDir, solution);
        }

        var solutionProps = BuildSolutionPropertyTokens(solution);
        var (apps, tests) = await ClassifyRunnablesAsync(projects, solutionDir, solutionProps, cancellationToken);

        var pick = PickRunnableProject(apps, tests, out var pickedTest);
        if (pick is not null)
        {
            if (pickedTest)
            {
                LogRunningLoneTestProject(pick, solution.Name);
            }

            return new RunInputResolution(WinAppRunMode.Project, pick, pick.Directory ?? solutionDir, solution);
        }

        // Zero or several runnable app projects → we don't emulate VS's startup-project selection;
        // require an explicit --project so the wrong app is never launched behind the user's back.
        // A lone test project auto-runs above; here we only reach the ambiguous/empty cases.
        var candidatePool = apps.Count > 0 ? apps : (tests.Count > 0 ? tests : projects);
        var candidateList = string.Join(", ", candidatePool
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        string reason;
        if (apps.Count > 1)
        {
            reason = $"'{solution.Name}' contains multiple runnable app projects ({candidateList})";
        }
        else if (tests.Count > 1)
        {
            reason = $"'{solution.Name}' contains only test projects ({candidateList})";
        }
        else
        {
            reason = $"No runnable app project was found in '{solution.Name}'";
        }

        throw new ProjectRunException(
            $"{reason}. Specify which project to run with --project <name>. Projects: {candidateList}.");
    }

    /// <summary>
    /// Classifies each candidate project into runnable apps and runnable test projects via
    /// <see cref="IProjectDetectionService.ClassifyRunnableAsync"/>. Non-runnable projects (libraries,
    /// etc.) are dropped. Test projects are kept separate so <see cref="PickRunnableProject"/> can prefer
    /// a real app but still fall back to a lone test project.
    /// </summary>
    private async Task<(List<FileInfo> Apps, List<FileInfo> Tests)> ClassifyRunnablesAsync(
        IReadOnlyList<FileInfo> projects,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string>? extraMsbuildProperties,
        CancellationToken cancellationToken)
    {
        var apps = new List<FileInfo>();
        var tests = new List<FileInfo>();
        foreach (var project in projects)
        {
            var kind = await projectDetectionService.ClassifyRunnableAsync(project, workingDirectory, extraMsbuildProperties, cancellationToken);
            switch (kind)
            {
                case ProjectRunnability.App:
                    apps.Add(project);
                    break;
                case ProjectRunnability.Test:
                    tests.Add(project);
                    break;
            }
        }

        return (apps, tests);
    }

    /// <summary>
    /// Picks the project to run from the classified candidates without emulating VS's startup-project
    /// selection: a single real app wins; test projects are skipped when any app exists; a lone test
    /// project (tests-only solution) is run as a convenience. Any other shape (several apps, several
    /// tests-only, none) returns null so the caller can require an explicit <c>--project</c>.
    /// </summary>
    private static FileInfo? PickRunnableProject(List<FileInfo> apps, List<FileInfo> tests, out bool pickedTestProject)
    {
        pickedTestProject = false;

        if (apps.Count == 1)
        {
            return apps[0];
        }

        if (apps.Count == 0 && tests.Count == 1)
        {
            pickedTestProject = true;
            return tests[0];
        }

        return null;
    }

    private void LogRunningLoneTestProject(FileInfo project, string sourceName)
    {
        ansiConsole.MarkupLineInterpolated(
            $"{UiSymbols.Note} No runnable app project found in '{sourceName}'; running the only runnable project '{project.Name}', which is a test project.");
    }

    /// <summary>
    /// Lists the C# projects in a solution via <c>dotnet sln &lt;sln&gt; list</c>, resolving each to an
    /// absolute <see cref="FileInfo"/>. Non-<c>.csproj</c> projects (e.g. <c>.vcxproj</c>) are excluded
    /// because <c>winapp run</c> builds and launches managed app projects.
    /// </summary>
    private async Task<List<FileInfo>> GetSolutionProjectsAsync(FileInfo solution, DirectoryInfo solutionDir, CancellationToken cancellationToken)
    {
        // Check for a capable SDK first: 'dotnet sln list' below also needs the SDK, and its failure
        // message ("could not read the solution") is far less actionable than the SDK guidance.
        var sdkError = await CheckSdkAsync(solutionDir, cancellationToken);
        if (sdkError != null)
        {
            throw new ProjectRunException(sdkError);
        }

        var arguments = WindowsCommandLine.JoinArguments(["sln", solution.FullName, "list"]) ?? string.Empty;

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(solutionDir, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' failed): {ex.Message}");
        }

        if (exitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            throw new ProjectRunException(
                $"Could not read the solution '{solution.Name}' ('dotnet sln list' exited {exitCode}). {detail}".TrimEnd());
        }

        var projects = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Skip the `dotnet sln list` header ("Project(s)" and its dashed underline).
            if (raw.All(c => c == '-') || string.Equals(raw, "Project(s)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!raw.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(solutionDir.FullName, raw));
            if (seen.Add(full))
            {
                projects.Add(new FileInfo(full));
            }
        }

        return projects;
    }

    /// <summary>
    /// Matches a <c>--project</c> selector against candidate projects by full path, file name (with or
    /// without the <c>.csproj</c> extension). Returns the single match, or null when zero or several
    /// candidates match (ambiguous).
    /// </summary>
    internal static FileInfo? MatchProjectSelector(IReadOnlyList<FileInfo> projects, string selector, DirectoryInfo baseDir)
    {
        var trimmed = selector.Trim();
        // Resolve a path-style selector against the input/solution directory (not the process cwd),
        // so `--project src/App/App.csproj` means "relative to what the user pointed winapp at".
        var rooted = Path.GetFullPath(trimmed, baseDir.FullName);
        var matches = projects.Where(p =>
            string.Equals(p.FullName, rooted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(p.Name), trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A *relative* path-style selector may not be rooted where we computed; fall back to a name
        // match on the selector's leaf so `--project src/App/App.csproj` still resolves. Skip this for a
        // fully qualified path: the user named an exact location, so silently matching a same-named
        // project elsewhere would be wrong (e.g. `--project C:\wrong\App.csproj` must not pick the
        // solution's unrelated `App.csproj`).
        if (matches.Count == 0 && !Path.IsPathFullyQualified(trimmed))
        {
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(leaf))
            {
                matches = projects.Where(p =>
                    string.Equals(p.Name, leaf, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(p.Name), Path.GetFileNameWithoutExtension(leaf), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <inheritdoc />
    public async Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        const string upgradeHint =
            "Running csproj requires .NET SDK 8.0.100 or newer. Install or update it from https://aka.ms/dotnet/download.";

        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, "--version", cancellationToken);
        }
        catch (Exception)
        {
            // dotnet not on PATH → Process.Start throws.
            return $"The .NET SDK was not found. {upgradeHint}";
        }

        if (exitCode != 0)
        {
            return $"Could not determine the .NET SDK version ('dotnet --version' failed). {upgradeHint}";
        }

        var versionLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(versionLine) && TryParseSdkVersion(versionLine, out var major, out var minor, out var patch))
        {
            var capable = major > 8 || (major == 8 && (minor > 0 || (minor == 0 && patch >= 100)));
            if (!capable)
            {
                return $"The .NET SDK {versionLine} is too old for project mode. {upgradeHint}";
            }
        }

        // Present but unparseable version → assume a modern SDK; the build will surface a real error
        // if --getProperty is genuinely unsupported.
        return null;
    }

    /// <summary>
    /// SHIM (temporary): resolves the <c>CsWinRTWindowsMetadata</c> folder to inject for SDK-less builds,
    /// or <c>null</c> when the user already set the property (their value wins) or no injection is
    /// needed/possible. See <see cref="CsWinRTMetadataShimService"/>.
    /// </summary>
    private string? ResolveCsWinRTMetadataShim(ProjectRunOptions options)
    {
        if (UserSetCsWinRTMetadata(options))
        {
            return null;
        }

        return csWinRTMetadataShimService.ResolveMetadataFolder(options.Framework);
    }

    /// <summary>
    /// True when the user supplied their own <c>-p:CsWinRTWindowsMetadata=…</c>; their value wins and the
    /// shim must not inject (or trigger a restore to resolve) anything.
    /// </summary>
    private static bool UserSetCsWinRTMetadata(ProjectRunOptions options) =>
        options.Properties.Any(p =>
            p.StartsWith("CsWinRTWindowsMetadata=", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        // SHIM (temporary): on hosts with no registered Windows SDK, resolve a folder of ref-pack winmds
        // to inject as -p:CsWinRTWindowsMetadata so C#/WinRT authoring projects build (see
        // CsWinRTMetadataShimService). Skipped when the user already set the property. null = no injection.
        var csWinRTMetadata = ResolveCsWinRTMetadataShim(options);
        var buildOptions = options;

        // Restore ordering: when the target lives in a solution, restore the whole solution's managed
        // projects up front so build-dependency siblings that are NOT ProjectReferences of the target
        // (e.g. an out-of-process COM server built by a custom MSBuild target) have a project.assets.json
        // and the build doesn't fail with NETSDK1004 — matching what VS / `dotnet build <sln>` do. The
        // SHIM restore below then covers the ref pack on genuinely clean SDK-less hosts. Both are gated on
        // actually building AND the user not opting out of restore.
        if (!options.NoBuild && !options.NoRestore)
        {
            // (1) Restore the owning solution's managed siblings. When it restores the whole solution
            // (all-managed) it also restored the target, so the passes below can skip their own restore.
            var restoredWholeSolution = await RestoreSolutionSiblingsAsync(csproj, options, workingDir, cancellationToken);

            // (2) SHIM (temporary) — ref-pack ordering: on a genuinely clean SDK-less host the ref pack
            // (Microsoft.Windows.SDK.NET.Ref) may not be on disk yet when we first resolve the shim, so the
            // shim no-ops and the very first `dotnet build` — handed no CsWinRTWindowsMetadata — fails even
            // though that same build restores the ref pack; only a SECOND invocation (cache warm) succeeds.
            // Pre-populate the ref pack with an explicit restore, then re-resolve so the first build gets the
            // winmd folder. Only fires when the shim would otherwise inject (no SDK registered) and the user
            // didn't set the property himself.
            if (csWinRTMetadata is null
                && !UserSetCsWinRTMetadata(options)
                && csWinRTMetadataShimService.IsWindowsSdkAbsent())
            {
                var restoreExit = restoredWholeSolution
                    ? 0
                    : await RunRestorePassAsync(csproj, options, workingDir, cancellationToken);
                if (restoreExit == 0)
                {
                    csWinRTMetadata = ResolveCsWinRTMetadataShim(options);
                    // The explicit restore already populated the cache; skip the redundant restore in the
                    // build pass so we don't restore twice.
                    buildOptions = options with { NoRestore = true };
                }
            }
            else if (restoredWholeSolution)
            {
                // The whole-solution restore already covered the target; skip the build pass's own restore.
                buildOptions = options with { NoRestore = true };
            }
        }

        // Two passes (spec §8.2, Change #1): (1) BUILD — a plain `dotnet build` whose console log
        // STREAMS live so the user sees progress (skipped under --no-build); then (2) EVALUATE — a
        // fast `dotnet msbuild --getProperty` that returns the resolved output paths as JSON. The
        // split is required because `--getProperty` SUPPRESSES normal MSBuild console output, so a
        // single combined pass would build silently. The evaluate pass is fed the SAME effective
        // Configuration/RID/Platform/TFM/-p as the build so its TargetDir/RunCommand match what was
        // actually built.
        if (!options.NoBuild)
        {
            var useLiveSpinner = ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger);
            var buildExit = await RunBuildPassAsync(csproj, buildOptions, workingDir, useLiveSpinner, csWinRTMetadata, cancellationToken);
            if (buildExit != 0)
            {
                // dotnet's diagnostics were already streamed live (or dumped on the spinner-failure
                // path); just log the summary and propagate the exit code — do not attempt to launch.
                logger.LogError("{UISymbol} Build failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, buildExit);
                return new ProjectBuildOutcome(null, buildExit);
            }
        }

        var evaluateArgs = BuildEvaluateArguments(csproj, options, csWinRTMetadata);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, evaluateArgs);

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);

        if (exitCode != 0)
        {
            // The build (if any) succeeded but property evaluation failed — surface dotnet's
            // diagnostics and propagate the exit code rather than launch against unknown output.
            logger.LogError("{UISymbol} Could not evaluate project properties for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, exitCode);
            var combined = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                // Keep stdout clean for --json consumers; route diagnostics to stderr instead.
                if (options.Json)
                {
                    Console.Error.WriteLine(combined);
                }
                else
                {
                    ansiConsole.WriteLine(combined);
                }
            }

            return new ProjectBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) &&
            !string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' is not a runnable project (OutputType='{outputType}'). 'winapp run' requires an executable project (OutputType Exe or WinExe).");
        }

        var targetDir = GetProp(props, "TargetDir");
        var runCommand = GetProp(props, "RunCommand");
        var selfContained = string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase);
        var packaging = DeterminePackaging(props, targetDir);

        if (string.IsNullOrEmpty(targetDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (TargetDir) for '{csproj.Name}'. Ensure the project builds successfully.");
        }

        if (packaging == ProjectPackaging.Unpackaged)
        {
            if (string.IsNullOrEmpty(runCommand) || !File.Exists(runCommand))
            {
                var reason = options.NoBuild
                    ? "The runnable executable was not found. Remove --no-build so the project is built first, or build it manually."
                    : "The build did not produce a runnable executable (RunCommand).";
                throw new ProjectRunException(
                    $"'{csproj.Name}' resolves to an unpackaged app but no launchable .exe is available. {reason}");
            }
        }

        var resolution = new ProjectRunResolution(
            csproj,
            targetDir,
            string.IsNullOrEmpty(runCommand) ? null : runCommand,
            packaging,
            selfContained,
            options.Architecture,
            options.Framework);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsDefinitivelyUnpackagedAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        // Reuse the exact evaluate pass (same -p/RID/Platform/TFM/shim as a real build) so the
        // WindowsPackageType we read matches what the build would see. It is evaluate-only — no build
        // is triggered — which is why this is cheap enough to run before deciding to build.
        var evaluateArgs = BuildEvaluateArguments(csproj, options, ResolveCsWinRTMetadataShim(options));
        var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, evaluateArgs, cancellationToken);
        if (exitCode != 0)
        {
            // Evaluation failed → indeterminate. Don't fail fast; let the normal build + authoritative
            // gate surface the real error and classify packaging.
            return false;
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        // Only an EXPLICIT WindowsPackageType=None is treated as definitive. An unset value is NOT —
        // a packaged app that declares identity via an emitted recipe (rather than the property) also
        // evaluates empty here pre-build, so DeterminePackaging's post-build recipe fallback must stay
        // authoritative. Reporting "unpackaged" on empty would misclassify that app and wrongly reject
        // its packaged-only options.
        return string.Equals(GetProp(props, "WindowsPackageType"), "None", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines packaged vs unpackaged from the evaluated properties (spec §7.1), never from
    /// manifest presence.
    /// </summary>
    private static ProjectPackaging DeterminePackaging(IReadOnlyDictionary<string, string> props, string targetDir)
    {
        var windowsPackageType = GetProp(props, "WindowsPackageType");

        if (string.Equals(windowsPackageType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Unpackaged;
        }

        if (!string.IsNullOrEmpty(windowsPackageType))
        {
            // MSIX (or any other non-empty value) → packaged.
            return ProjectPackaging.Packaged;
        }

        // Unset/empty (common on the --no-build evaluate-only path, where MSIX targets don't run):
        // fall back to EnableMsixTooling or an emitted recipe.
        if (string.Equals(GetProp(props, "EnableMsixTooling"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                if (Directory.EnumerateFiles(targetDir, "*.build.appxrecipe").Any())
                {
                    return ProjectPackaging.Packaged;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Ignore — fall through to unpackaged.
            }
        }

        return ProjectPackaging.Unpackaged;
    }

    /// <summary>
    /// SHIM (temporary) — restore ordering: runs an explicit <c>dotnet restore</c> so the
    /// <c>Microsoft.Windows.SDK.NET.Ref</c> ref pack lands on disk BEFORE the shim resolves its winmd
    /// folder, fixing the clean-host first-build failure (see <c>BuildAndResolveAsync</c>). Output is
    /// captured (not streamed) since it's a fast pre-step; the following build pass streams as usual.
    /// Returns the dotnet exit code so the caller only skips the build's own restore when this succeeded.
    /// </summary>
    private async Task<int> RunRestorePassAsync(FileInfo csproj, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        var restoreArgs = BuildRestorePassArguments(csproj, options);
        logger.LogDebug("{UISymbol} Restoring before SDK-less CsWinRT metadata resolution: dotnet {Arguments}", UiSymbols.Note, restoreArgs);
        var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, restoreArgs, cancellationToken);
        if (exitCode != 0)
        {
            // Non-fatal: fall through and let the build pass restore + surface any real error itself.
            logger.LogDebug("{UISymbol} Pre-shim restore exited {ExitCode}; deferring to the build pass.", UiSymbols.Note, exitCode);
        }
        return exitCode;
    }

    /// <summary>
    /// Restores the owning solution's managed sibling projects before the target build so build-dependency
    /// siblings that are not <c>ProjectReference</c>s of the target still have a <c>project.assets.json</c>
    /// (NETSDK1004 parity with VS / <c>dotnet build &lt;sln&gt;</c>). Only fires for a solution-resolved run
    /// (<see cref="ProjectRunOptions.Solution"/> non-null). When every listed project is managed, a single
    /// <c>dotnet restore &lt;sln&gt;</c> restores the whole graph (including the target) and this returns
    /// <see langword="true"/> so the caller can skip the build pass's own restore. When a native project
    /// (<c>.vcxproj</c>/<c>.wapproj</c>/<c>.shproj</c>) is present — which <c>dotnet restore</c> can't handle
    /// on a VS-less box — the managed siblings are restored individually (the target is left to the normal
    /// restore) and this returns <see langword="false"/>. All restores are non-fatal (best-effort); the
    /// build pass surfaces any real error.
    /// </summary>
    private async Task<bool> RestoreSolutionSiblingsAsync(FileInfo target, ProjectRunOptions options, DirectoryInfo workingDir, CancellationToken cancellationToken)
    {
        if (options.Solution is null)
        {
            return false;
        }

        var (allManaged, siblings) = ComputeSolutionRestorePlan(options.Solution, target);
        if (siblings.Count == 0)
        {
            // Solution lists only the target (or only native siblings) — nothing extra to restore; the
            // normal target restore is unchanged.
            return false;
        }

        if (allManaged)
        {
            // Closest to VS: one restore over the whole solution pulls the target and every sibling.
            var args = BuildRestorePassArguments(options.Solution, options);
            logger.LogDebug("{UISymbol} Restoring solution before build (build-dependency parity): dotnet {Arguments}", UiSymbols.Note, args);
            var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode != 0)
            {
                logger.LogDebug("{UISymbol} Solution restore exited {ExitCode}; deferring to per-project restore.", UiSymbols.Note, exitCode);
            }
            return exitCode == 0;
        }

        // A native project is present, so `dotnet restore <sln>` would error on a VS-less host. Restore the
        // managed siblings individually and skip the natives; the target is restored by the normal pass.
        foreach (var sibling in siblings)
        {
            var args = BuildRestorePassArguments(sibling, options);
            logger.LogDebug("{UISymbol} Restoring solution sibling before build (build-dependency parity): dotnet {Arguments}", UiSymbols.Note, args);
            var (exitCode, _, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode != 0)
            {
                logger.LogDebug("{UISymbol} Sibling restore of {Sibling} exited {ExitCode}; continuing.", UiSymbols.Note, sibling.Name, exitCode);
            }
        }

        return false;
    }
    /// <summary>
    /// Builds the argument string for the SHIM's pre-build <c>dotnet restore</c>. It mirrors the build
    /// pass's RID / user <c>-p</c> / solution properties so the same graph restores, but omits
    /// <c>-c</c>/<c>-f</c>/<c>-v</c> (restore is TFM- and config-agnostic for pulling the ref pack) and
    /// never adds <c>--no-restore</c>. Pure and unit-testable.
    /// </summary>
    internal static string BuildRestorePassArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var tokens = new List<string>
        {
            "restore",
            csproj.FullName,
            "-r",
            rid,
        };

        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the argument string for the streaming BUILD pass: a plain <c>dotnet build</c> that
    /// produces the output and STREAMS its console log. It deliberately omits <c>--getProperty</c>
    /// (which suppresses that log) and needs no explicit <c>-t:Build</c> (Build is the default
    /// target). The dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> switches always beat a same-named user
    /// <c>-p</c>. Architecture is conveyed by the RID (<c>-r win-&lt;arch&gt;</c>) ONLY — project mode
    /// does NOT force a global <c>-p:Platform</c> (nor its <c>EnableDynamicPlatformResolution</c>
    /// companion), matching how Visual Studio and a plain <c>dotnet build -r win-&lt;arch&gt;</c> convey
    /// arch. A forced global Platform de-synchronizes a no-<c>&lt;Platforms&gt;</c> WinUI library
    /// reference (its XAML/MRT outputs compile to the AnyCPU <c>bin\Debug\…</c> path while the app's
    /// Platform-driven lookup expects <c>bin\&lt;arch&gt;\Debug\…</c>) → MSB3030/PRI252. The RID alone
    /// still yields the correct packaged manifest <c>ProcessorArchitecture</c> and apphost arch. A user
    /// who explicitly passes <c>-p:Platform=…</c>/<c>-p:EnableDynamicPlatformResolution=…</c> still has
    /// it forwarded (via the user <c>-p</c> loop). The <c>-v</c> verbosity is mapped from the CLI's log
    /// level (Change #1, spec §8.3/§8.5).
    /// </summary>
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity, string? csWinRTMetadataFolder = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        var tokens = new List<string>
        {
            "build",
            csproj.FullName,
            "-c",
            options.Configuration,
            "-r",
            rid,
        };

        if (options.NoRestore)
        {
            tokens.Add("--no-restore");
        }

        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add("-f");
            tokens.Add(options.Framework);
        }

        tokens.Add("-v");
        tokens.Add(verbosity);

        // User -p properties come FIRST; the dedicated -c/-r/-f switches above always beat a
        // same-named -p (see WarnOnOverriddenFlags). A user-supplied -p:Platform / EDPR flows through
        // here and is respected — project mode itself never injects them.
        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        // When the target was resolved from a solution, define $(SolutionDir) and its siblings so
        // projects that reference them build exactly as they do under `dotnet build <sln>` / VS.
        AppendSolutionProperties(tokens, options);

        // SHIM (temporary): inject the resolved ref-pack winmd folder so cswinrt.exe can find contract
        // winmds without a registered Windows SDK. Only present when the shim resolved a folder (SDK
        // absent + ref pack restored) and the user didn't set the property. See CsWinRTMetadataShimService.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the argument string for the project-mode EVALUATE pass: a fast, side-effect-free
    /// <c>dotnet msbuild --getProperty</c> that returns the resolved output paths as JSON. It is the
    /// same shape used on the <c>--no-build</c> path and is fed the SAME effective build inputs as the
    /// build pass so its <c>TargetDir</c>/<c>RunCommand</c> match what was built. <c>dotnet msbuild</c>
    /// rejects <c>-c</c>/<c>-r</c> (MSB1001), so Configuration/RID/TFM are passed as <c>-p:</c> and are
    /// emitted LAST so MSBuild's last-wins makes a dedicated value beat a conflicting user <c>-p</c>
    /// (spec §8.2/M2).
    /// </summary>
    internal static string BuildEvaluateArguments(FileInfo csproj, ProjectRunOptions options, string? csWinRTMetadataFolder = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        // User -p first so the dedicated equivalents below win on a conflict (MSBuild is last-wins).
        // A user-supplied -p:Platform / EDPR flows through here and is respected; project mode never
        // injects them (arch is conveyed by RuntimeIdentifier only — see BuildBuildPassArguments).
        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        // Match the build pass: define $(SolutionDir) & siblings so the evaluated TargetDir/RunCommand
        // resolve against the same solution-anchored inputs as the build (solution mode only).
        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        tokens.Add($"-p:RuntimeIdentifier={rid}");
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        // SHIM (temporary): keep the evaluate pass's inputs identical to the build pass — inject the same
        // CsWinRTWindowsMetadata folder when the shim resolved one. See CsWinRTMetadataShimService.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        foreach (var name in RequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Appends the <c>Solution*</c> MSBuild properties a solution build normally sets — most
    /// importantly <c>$(SolutionDir)</c> — when the run target was resolved from a solution. Building
    /// a bare <c>.csproj</c> leaves these undefined, so projects that reference them (shared prop
    /// imports, output paths) fail; defining them here builds the project the same way it builds under
    /// <c>dotnet build &lt;sln&gt;</c> / Visual Studio. No-op for a bare <c>.csproj</c> target.
    /// </summary>
    private static void AppendSolutionProperties(List<string> tokens, ProjectRunOptions options)
    {
        if (options.Solution is not { } solution)
        {
            return;
        }

        // User -p properties are emitted before this call, and MSBuild is last-wins, so re-emitting a
        // Solution* property the user already set would clobber their value. Skip any the user specified
        // so an explicit `-p:SolutionDir=…` (or sibling) always wins. Covers every solution-attached
        // target — bare-.csproj-with-owning-sln, directory-resolved, and explicit-solution alike.
        foreach (var token in BuildSolutionPropertyTokens(solution))
        {
            if (UserSpecifiesProperty(options.Properties, SolutionPropertyName(token)))
            {
                continue;
            }

            tokens.Add(token);
        }
    }

    /// <summary>True when the user passed a <c>-p Name=Value</c> for <paramref name="name"/> (case-insensitive).</summary>
    private static bool UserSpecifiesProperty(IReadOnlyList<string> properties, string name) =>
        properties.Any(p => p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts the property name from a <c>-p:Name=Value</c> token (e.g. <c>SolutionDir</c>).</summary>
    private static string SolutionPropertyName(string token)
    {
        var start = token.StartsWith("-p:", StringComparison.Ordinal) ? 3 : 0;
        var equals = token.IndexOf('=', start);
        return equals > start ? token[start..equals] : token[start..];
    }

    /// <summary>
    /// Builds the <c>-p:Solution*</c> MSBuild property tokens a solution build normally sets — most
    /// importantly <c>$(SolutionDir)</c> (trailing separator, per MSBuild convention). Shared by the
    /// build pass, the evaluation pass, and project classification so all three see the same
    /// solution-defined properties.
    /// </summary>
    private static IReadOnlyList<string> BuildSolutionPropertyTokens(FileInfo solution)
    {
        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        // MSBuild's $(SolutionDir) convention is a trailing directory separator. EscapeArgument
        // doubles a trailing backslash before a closing quote, so a quoted value round-trips exactly.
        if (!solutionDir.EndsWith(Path.DirectorySeparatorChar) && !solutionDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            solutionDir += Path.DirectorySeparatorChar;
        }

        var solutionName = Path.GetFileNameWithoutExtension(solution.Name);

        return
        [
            $"-p:SolutionDir={solutionDir}",
            $"-p:SolutionPath={solution.FullName}",
            $"-p:SolutionName={solutionName}",
            $"-p:SolutionFileName={solution.Name}",
            $"-p:SolutionExt={solution.Extension}",
        ];
    }
    /// <list type="bullet">
    ///   <item><c>--json</c>: stream to stderr only so stdout stays pure JSON — no banner, no spinner.</item>
    ///   <item>Interactive terminal, non-verbose: animate a Spectre status spinner and hide the raw
    ///   build lines; on failure, dump the captured output so the MSBuild error is visible.</item>
    ///   <item>Otherwise (verbose, or an agent/CI/redirected terminal): print a single "Building…"
    ///   line and stream dotnet's output live (plain lines — no spinner-frame flooding).</item>
    /// </list>
    /// </summary>
    internal async Task<int> RunBuildPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        bool useLiveSpinner,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var buildArgs = BuildBuildPassArguments(csproj, options, verbosity, csWinRTMetadataFolder);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, buildArgs);

        var banner = $"Building {csproj.Name} ({options.Configuration} | {options.Architecture})...";

        // --json: stdout must stay pure JSON, so route ALL build output to stderr and show no banner.
        // Console.Error is synchronized, so the concurrent stdout/stderr callbacks are safe.
        if (options.Json)
        {
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        // Interactive human, non-verbose: animate a spinner and keep the raw build lines hidden,
        // revealing the (bounded) captured output only if the build fails.
        if (useLiveSpinner && !logger.IsEnabled(LogLevel.Debug))
        {
            var captured = new List<string>();
            void Capture(string line)
            {
                lock (captured)
                {
                    captured.Add(line);
                    if (captured.Count > MaxBuildTailLines)
                    {
                        captured.RemoveAt(0);
                    }
                }
            }

            var spinnerExit = await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync(banner, async _ =>
                    await dotNetService.RunDotnetStreamingAsync(
                        workingDir, buildArgs, Capture, Capture, cancellationToken));

            if (spinnerExit != 0)
            {
                foreach (var line in captured)
                {
                    ansiConsole.WriteLine(line);
                }
            }

            return spinnerExit;
        }

        // Verbose, or a non-interactive/agent/CI terminal: a single static line + live streamed output.
        // Serialize the writes so the concurrent stdout/stderr callbacks don't interleave.
        //
        // --quiet (Information suppressed) must keep stdout clean like --json: skip the banner and
        // route build output to stderr so failures stay visible without polluting stdout.
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return await dotNetService.RunDotnetStreamingAsync(
                workingDir, buildArgs,
                onOutputLine: static line => Console.Error.WriteLine(line),
                onErrorLine: static line => Console.Error.WriteLine(line),
                cancellationToken);
        }

        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} {banner}");
        var writeLock = new object();
        void WriteLive(string line)
        {
            lock (writeLock)
            {
                ansiConsole.WriteLine(line);
            }
        }

        return await dotNetService.RunDotnetStreamingAsync(
            workingDir, buildArgs, WriteLive, WriteLive, cancellationToken);
    }

    /// <summary>
    /// Maps the CLI's effective log level to a dotnet <c>-v</c> verbosity for the build pass so that
    /// <c>--verbose</c> reaches dotnet (Change #1): trace ⇒ detailed, verbose ⇒ normal, <c>--quiet</c>
    /// ⇒ quiet; otherwise minimal to keep ordinary runs tidy.
    /// </summary>
    private static string ResolveBuildVerbosity(ILogger logger, bool json)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "detailed";
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            return "normal";
        }

        // --quiet suppresses Information (and is never combined with --json); keep dotnet quiet too.
        if (!json && !logger.IsEnabled(LogLevel.Information))
        {
            return "quiet";
        }

        return "minimal";
    }

    private void WarnOnOverriddenFlags(ProjectRunOptions options)
    {
        // Match dotnet's behavior (dedicated flag wins over a same-named -p) but leave a debug trail.
        foreach (var property in options.Properties)
        {
            var name = property.Split('=', 2)[0].Trim();
            if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "{UISymbol} -p:{Property} is overridden by the dedicated flag (matches dotnet precedence).",
                    UiSymbols.Note, property);
            }
            else if (name.Equals("Platform", StringComparison.OrdinalIgnoreCase))
            {
                // Project mode conveys arch via the RuntimeIdentifier only and does NOT inject a global
                // Platform, so a user -p:Platform is forwarded as-is. The RID still follows --arch, so an
                // inconsistent pair (e.g. --arch x86 -p:Platform=ARM64) builds a mismatched app — warn so
                // the divergence isn't silent. Note: forcing -p:Platform on a multi-project WinUI app can
                // reintroduce the MSB3030/PRI252 split with no-<Platforms> library references.
                logger.LogDebug(
                    "{UISymbol} -p:{Property} is forwarded as-is; the RuntimeIdentifier still follows --arch, so ensure they are consistent.",
                    UiSymbols.Note, property);
            }
        }
    }

    private static string GetProp(IReadOnlyDictionary<string, string> props, string name)
        => props.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    /// <summary>
    /// Parses the leading <c>major.minor.patch</c> of a <c>dotnet --version</c> string
    /// (e.g. <c>8.0.100</c>, <c>10.0.301</c>, <c>8.0.100-preview.1</c>).
    /// </summary>
    internal static bool TryParseSdkVersion(string versionText, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        // Strip any prerelease/build suffix.
        var core = versionText.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        return int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }
}
