// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// Performs breadth-first detection of compatible projects in a directory tree.
/// Pure detection logic — no UI dependencies. Use <see cref="IProgress{T}"/> for live updates.
/// </summary>
internal sealed class ProjectDetectionService(ILogger<ProjectDetectionService> logger) : IProjectDetectionService
{
    /// <summary>
    /// Directory names that are skipped by default during project search.
    /// These are well-known output, cache, or dependency directories that
    /// are unlikely to contain user project roots.
    /// </summary>
    internal static readonly HashSet<string> DefaultIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "debug",
        "release",
        ".git",
        ".vs",
        ".vscode",
        ".idea",
        "packages",
        "dist",
        "build",
        "out",
        "target",
        ".winapp",
        "artifacts",
        "TestResults",
        "__pycache__",
        ".gradle",
        ".dart_tool",
        ".pub-cache",
        ".nuget",
        ".cargo",
    };

    /// <summary>
    /// Directory names that are always skipped during project search.
    /// These are either not real project directories or can cause infinite loops.
    /// </summary>
    private static readonly HashSet<string> HardIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
    };

    public DetectedProject? DetectProjectAt(DirectoryInfo directory)
    {
        return DetectProject(directory, directory);
    }

    public Task<IReadOnlyList<DetectedProject>> DetectProjectsAsync(
        DirectoryInfo root,
        int maxProjects,
        IProgress<DetectedProject>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<DetectedProject>();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(root);

        while (queue.Count > 0 && results.Count < maxProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = queue.Dequeue();

            var detected = DetectProject(current, root);
            if (detected != null)
            {
                results.Add(detected);
                progress?.Report(detected);
                logger.LogDebug("Detected {Type} project at {Path}", detected.TypeLabel, detected.DisplayPath);
                // Prune: don't search subdirectories of a detected project
                continue;
            }

            // Enqueue subdirectories for further searching
            EnqueueSubdirectories(queue, current, DefaultIgnoredDirectories);
        }

        return Task.FromResult<IReadOnlyList<DetectedProject>>(results);
    }

    /// <summary>
    /// Checks a single directory for all known project markers.
    /// Returns the most specific match, or null if no project is detected.
    /// Detection order follows enum specificity: Tauri > Electron > Flutter > Dotnet > Rust > CPP.
    /// </summary>
    internal static DetectedProject? DetectProject(DirectoryInfo directory, DirectoryInfo searchRoot)
    {
        var displayPath = GetRelativeDisplayPath(directory, searchRoot);

        // Tauri: check immediate subdirectories for tauri.conf.json
        var tauriConf = FindTauriConfFile(directory);
        if (tauriConf != null)
        {
            return new DetectedProject(DetectedProjectType.Tauri, directory, displayPath, tauriConf);
        }

        // Electron: package.json with electron dependency (check before generic markers)
        if (IsElectronProject(directory))
        {
            return new DetectedProject(DetectedProjectType.Electron, directory, displayPath, "package.json");
        }

        // Flutter: pubspec.yaml
        if (File.Exists(Path.Combine(directory.FullName, "pubspec.yaml")))
        {
            return new DetectedProject(DetectedProjectType.Flutter, directory, displayPath, "pubspec.yaml");
        }

        // .NET: *.csproj (only executable, non-test projects)
        var csprojName = FindExecutableCsproj(directory);
        if (csprojName != null)
        {
            return new DetectedProject(DetectedProjectType.Dotnet, directory, displayPath, csprojName);
        }

        // Rust: Cargo.toml
        if (File.Exists(Path.Combine(directory.FullName, "Cargo.toml")))
        {
            return new DetectedProject(DetectedProjectType.Rust, directory, displayPath, "Cargo.toml");
        }

        // C++: CMakeLists.txt
        if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
        {
            return new DetectedProject(DetectedProjectType.CPP, directory, displayPath, "CMakeLists.txt");
        }

        return null;
    }

    private static string? FindTauriConfFile(DirectoryInfo directory)
    {
        try
        {
            foreach (var subDir in directory.EnumerateDirectories())
            {
                // Skip symlinks/junctions to prevent reading outside the search root
                if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(subDir.FullName, "tauri.conf.json")))
                {
                    return $"{subDir.Name}/tauri.conf.json";
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (IOException)
        {
            // Skip directories with I/O errors
        }

        return null;
    }

    private static bool IsElectronProject(DirectoryInfo directory)
    {
        var packageJsonPath = Path.Combine(directory.FullName, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(packageJsonPath);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            return HasElectronDependency(root, "dependencies") ||
                   HasElectronDependency(root, "devDependencies");
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasElectronDependency(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var deps) &&
               deps.ValueKind == JsonValueKind.Object &&
               deps.TryGetProperty("electron", out _);
    }

    private static bool HasFileWithExtension(DirectoryInfo directory, string pattern)
    {
        try
        {
            return directory.EnumerateFiles(pattern).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the file name of the first executable .csproj in the directory,
    /// or null if none is found. Only executable (OutputType = Exe or WinExe), non-test projects qualify.
    /// </summary>
    internal static string? FindExecutableCsproj(DirectoryInfo directory)
    {
        IEnumerable<FileInfo> csprojFiles;
        try
        {
            csprojFiles = directory.EnumerateFiles("*.csproj");
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        foreach (var csproj in csprojFiles)
        {
            if (IsExecutableNonTestProject(csproj))
            {
                return csproj.Name;
            }
        }

        return null;
    }

    private static bool IsExecutableNonTestProject(FileInfo csprojFile)
    {
        try
        {
            var doc = XDocument.Load(csprojFile.FullName);
            // Use LocalName to match elements regardless of XML namespace
            // (SDK-style projects have no namespace; legacy .NET Framework projects use the MSBuild namespace)
            var propertyGroups = doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup");

            string? outputType = null;
            bool? isTestProject = null;

            foreach (var pg in propertyGroups)
            {
                var outputTypeEl = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputType");
                if (outputTypeEl != null)
                {
                    outputType = outputTypeEl.Value.Trim();
                }

                var isTestEl = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "IsTestProject");
                if (isTestEl != null)
                {
                    isTestProject = string.Equals(isTestEl.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            // Exclude test projects
            if (isTestProject == true)
            {
                return false;
            }

            // Only include executable projects (Exe or WinExe)
            if (outputType == null)
            {
                return false;
            }

            return string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If we can't parse the csproj, skip it
            return false;
        }
    }

    private void EnqueueSubdirectories(Queue<DirectoryInfo> queue, DirectoryInfo parent, HashSet<string> ignoredNames)
    {
        IEnumerable<DirectoryInfo> subdirs;
        try
        {
            subdirs = parent.EnumerateDirectories();
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot access directory: {Path}", parent.FullName);
            return;
        }
        catch (IOException ex)
        {
            logger.LogDebug("I/O error enumerating {Path}: {Message}", parent.FullName, ex.Message);
            return;
        }

        try
        {
            foreach (var subDir in subdirs)
            {
                try
                {
                    // Skip hidden directories (starting with .) that aren't in the explicit ignore list
                    if (subDir.Name.StartsWith('.') && !ignoredNames.Contains(subDir.Name))
                    {
                        logger.LogDebug("Skipping hidden directory: {Path}", subDir.FullName);
                        continue;
                    }

                    if (ignoredNames.Contains(subDir.Name))
                    {
                        logger.LogDebug("Skipping ignored directory: {Path}", subDir.FullName);
                        continue;
                    }

                    // Skip reparse points (symlinks, junctions) to avoid cycles
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        logger.LogDebug("Skipping reparse point: {Path}", subDir.FullName);
                        continue;
                    }

                    queue.Enqueue(subDir);
                }
                catch (UnauthorizedAccessException)
                {
                    logger.LogDebug("Cannot access directory: {Path}", subDir.FullName);
                }
                catch (IOException ex)
                {
                    logger.LogDebug("I/O error accessing {Path}: {Message}", subDir.FullName, ex.Message);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot access directory during enumeration: {Path}", parent.FullName);
        }
        catch (IOException ex)
        {
            logger.LogDebug("I/O error during enumeration of {Path}: {Message}", parent.FullName, ex.Message);
        }
    }

    private static string GetRelativeDisplayPath(DirectoryInfo directory, DirectoryInfo searchRoot)
    {
        var rootPath = searchRoot.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dirPath = directory.FullName.TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(dirPath + Path.DirectorySeparatorChar, rootPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dirPath, searchRoot.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (dirPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return dirPath[rootPath.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }

        return dirPath;
    }
}
