// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Input and solution resolution for <see cref="ProjectRunService" />: mapping a file/folder/solution
/// argument to a runnable project, and computing sibling-restore plans.
/// </summary>
internal sealed partial class ProjectRunService
{
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
            // Honor an explicit --project even when only one .csproj exists, so a mismatched selector is
            // a clear error rather than silently ignored. Attach the owning solution (walking up) so
            // $(SolutionDir) is defined exactly as on the bare-.csproj and multi-.csproj paths — otherwise
            // a lone project under a repo solution would build without the solution context VS gives it.
            if (!string.IsNullOrWhiteSpace(projectSelector)
                && MatchProjectSelector(csprojs, projectSelector, dir) is null)
            {
                throw new ProjectRunException(
                    $"--project '{projectSelector}' did not match '{csprojs[0].Name}' in '{dir.FullName}'.");
            }

            return new RunInputResolution(WinAppRunMode.Project, csprojs[0], dir, FindOwningSolution(csprojs[0]));
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

            return new RunInputResolution(WinAppRunMode.Project, selected, dir, FindOwningSolution(selected));
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

            return new RunInputResolution(WinAppRunMode.Project, dirPick, dir, FindOwningSolution(dirPick));
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

        // A *relative* path-style selector may not be rooted where we computed (e.g. the user gave a
        // path relative to a different base). Fall back to matching against the selector, but honor its
        // directory intent: if the selector names a directory component, suffix-match the full path so
        // `--project src/App/App.csproj` still resolves to …\src\App\App.csproj yet a *different*
        // same-named project (…\other\App.csproj) is NOT silently picked. Only a bare leaf selector
        // (no directory component) falls back to a name match. Skip this entirely for a fully qualified
        // path: the user named an exact location, so matching elsewhere would be wrong (e.g.
        // `--project C:\wrong\App.csproj` must not pick the solution's unrelated `App.csproj`).
        if (matches.Count == 0 && !Path.IsPathFullyQualified(trimmed))
        {
            var normalized = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (normalized.Contains(Path.DirectorySeparatorChar))
            {
                var suffix = Path.DirectorySeparatorChar + normalized;
                matches = projects.Where(p => p.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (!string.IsNullOrEmpty(normalized))
            {
                matches = projects.Where(p =>
                    string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(p.Name), Path.GetFileNameWithoutExtension(normalized), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }
}
