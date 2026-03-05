// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for detecting and working with .NET projects, using the dotnet CLI
/// </summary>
internal partial class DotNetService : IDotNetService
{
    /// <summary>
    /// Minimum Windows SDK version that supports WinAppSDK
    /// </summary>
    private const string MinWindowsSdkVersion = "10.0.17763.0";

    /// <summary>
    /// Recommended TargetFramework for new WinAppSDK projects
    /// </summary>
    private const string RecommendedTfm = "net10.0-windows10.0.26100.0";

    // NuGet package names for .NET WinAppSDK projects
    internal const string WINAPP_SDK_NUGET_PACKAGE = "Microsoft.WindowsAppSDK";

    internal const string WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE = "Microsoft.Windows.SDK.BuildTools.WinApp";

    [GeneratedRegex(@"^net(\d+\.\d+)-windows([\d.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsTfmRegex();

    [GeneratedRegex(@"^net(\d+\.\d+)-windows$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainWindowsTfmRegex();

    [GeneratedRegex(@"^net(\d+\.\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainNetTfmRegex();

    [GeneratedRegex(@"<TargetFramework>(.*?)</TargetFramework>", RegexOptions.Singleline)]
    private static partial Regex TargetFrameworkElementRegex();

    [GeneratedRegex(@"<TargetFrameworks>(.*?)</TargetFrameworks>", RegexOptions.Singleline)]
    private static partial Regex TargetFrameworksElementRegex();

    [GeneratedRegex(@"<RuntimeIdentifier[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeIdentifierElementRegex();

    [GeneratedRegex(@"<RuntimeIdentifiers[\s>].*?</RuntimeIdentifiers>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RuntimeIdentifiersElementRegex();

    public IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return [];
        }

        return directory.GetFiles("*.csproj", SearchOption.TopDirectoryOnly);
    }

    public string? GetTargetFramework(FileInfo csprojPath)
    {
        if (!csprojPath.Exists)
        {
            return null;
        }

        var content = File.ReadAllText(csprojPath.FullName);

        // Check singular <TargetFramework> first
        var match = TargetFrameworkElementRegex().Match(content);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Fall back to <TargetFrameworks> — return the first TFM from the semicolon-separated list
        var pluralMatch = TargetFrameworksElementRegex().Match(content);
        if (pluralMatch.Success)
        {
            var first = pluralMatch.Groups[1].Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return first;
        }

        return null;
    }

    public bool IsMultiTargeted(FileInfo csprojPath)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = File.ReadAllText(csprojPath.FullName);
        return TargetFrameworksElementRegex().IsMatch(content);
    }

    public bool IsTargetFrameworkSupported(string targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        var match = WindowsTfmRegex().Match(targetFramework);
        if (!match.Success)
        {
            // Not a windows TFM (e.g. "net8.0" without -windows)
            return false;
        }

        var netVersion = match.Groups[1].Value;
        var windowsVersion = match.Groups[2].Value;

        // We need at least .NET 6.0
        if (!Version.TryParse(netVersion, out var parsedNetVersion) || parsedNetVersion < new Version(6, 0))
        {
            return false;
        }

        // We need at least Windows SDK 10.0.17763.0
        if (!Version.TryParse(windowsVersion, out var parsedWinVersion) ||
            !Version.TryParse(MinWindowsSdkVersion, out var minWinVersion))
        {
            return false;
        }

        return parsedWinVersion >= minWinVersion;
    }

    public string GetRecommendedTargetFramework(string? currentTargetFramework = null)
    {
        // Default Windows SDK version to use
        const string defaultWindowsSdkVersion = "10.0.26100.0";

        if (string.IsNullOrWhiteSpace(currentTargetFramework))
        {
            return RecommendedTfm;
        }

        // Try to parse the current TFM to extract .NET version and optional Windows version
        var windowsTfmMatch = WindowsTfmRegex().Match(currentTargetFramework);
        if (windowsTfmMatch.Success)
        {
            // Already a Windows TFM (e.g., net10.0-windows10.0.26100.0)
            var netVersion = windowsTfmMatch.Groups[1].Value;
            var windowsVersion = windowsTfmMatch.Groups[2].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Check if Windows SDK version is supported
                if (Version.TryParse(windowsVersion, out var parsedWinVersion) &&
                    Version.TryParse(MinWindowsSdkVersion, out var minWinVersion) &&
                    parsedWinVersion >= minWinVersion)
                {
                    // Current TFM is already fully supported
                    return currentTargetFramework;
                }

                // Keep .NET version, update Windows SDK version
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Try to match a plain Windows TFM without SDK version (e.g., net10.0-windows)
        var plainWindowsMatch = PlainWindowsTfmRegex().Match(currentTargetFramework);
        if (plainWindowsMatch.Success)
        {
            var netVersion = plainWindowsMatch.Groups[1].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Keep .NET version, add Windows SDK version
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Try to match a plain .NET TFM (e.g., net8.0)
        var plainNetMatch = PlainNetTfmRegex().Match(currentTargetFramework);
        if (plainNetMatch.Success)
        {
            var netVersion = plainNetMatch.Groups[1].Value;

            // Check if the .NET version is supported (>= 6.0)
            if (Version.TryParse(netVersion, out var parsedNetVersion) && parsedNetVersion >= new Version(6, 0))
            {
                // Keep .NET version, add Windows TFM
                return $"net{netVersion}-windows{defaultWindowsSdkVersion}";
            }
        }

        // Fallback to default recommended TFM
        return RecommendedTfm;
    }

    public void SetTargetFramework(FileInfo csprojPath, string newTargetFramework)
    {
        var content = File.ReadAllText(csprojPath.FullName);
        var match = TargetFrameworkElementRegex().Match(content);

        if (match.Success)
        {
            content = content[..match.Index]
                + $"<TargetFramework>{newTargetFramework}</TargetFramework>"
                + content[(match.Index + match.Length)..];
        }
        else
        {
            // No TargetFramework element exists; insert one into the first PropertyGroup
            var propGroupIdx = content.IndexOf("<PropertyGroup", StringComparison.OrdinalIgnoreCase);
            if (propGroupIdx >= 0)
            {
                // Find the closing > of the <PropertyGroup> tag
                var closeTag = content.IndexOf('>', propGroupIdx);
                if (closeTag >= 0)
                {
                    var insertPos = closeTag + 1;
                    content = content[..insertPos]
                        + Environment.NewLine + $"    <TargetFramework>{newTargetFramework}</TargetFramework>"
                        + content[insertPos..];
                }
            }
        }

        File.WriteAllText(csprojPath.FullName, content);
    }

    public async Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);

        // Don't modify if the project already defines RuntimeIdentifier (singular)
        if (RuntimeIdentifierElementRegex().IsMatch(content))
        {
            return false;
        }

        // Insert a RuntimeIdentifier with a Condition so it only applies when not already set
        // (e.g. via command-line -r or Directory.Build.props)
        const string runtimeIdentifierElement =
            "<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == ''\">win-$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())</RuntimeIdentifier>";

        // Insert into the first PropertyGroup:
        // 1. After <RuntimeIdentifiers> if present (keep RID properties together)
        // 2. After <TargetFramework> if present
        // 3. At start of first PropertyGroup as last resort
        var ridsMatch = RuntimeIdentifiersElementRegex().Match(content);
        if (ridsMatch.Success)
        {
            // Insert right after the </RuntimeIdentifiers> element
            var insertPos = ridsMatch.Index + ridsMatch.Length;
            content = content[..insertPos]
                + Environment.NewLine + "    " + runtimeIdentifierElement
                + content[insertPos..];
        }
        else
        {
            var tfmMatch = TargetFrameworkElementRegex().Match(content);
            if (tfmMatch.Success)
            {
                // Insert after the TargetFramework line
                var insertPos = tfmMatch.Index + tfmMatch.Length;
                content = content[..insertPos]
                    + Environment.NewLine + "    " + runtimeIdentifierElement
                    + content[insertPos..];
            }
            else
            {
                // No TargetFramework found; insert at start of first PropertyGroup
                var propGroupIdx = content.IndexOf("<PropertyGroup", StringComparison.OrdinalIgnoreCase);
                if (propGroupIdx >= 0)
                {
                    var closeTag = content.IndexOf('>', propGroupIdx);
                    if (closeTag >= 0)
                    {
                        var insertPos = closeTag + 1;
                        content = content[..insertPos]
                            + Environment.NewLine + "    " + runtimeIdentifierElement
                            + content[insertPos..];
                    }
                }
            }
        }

        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    [GeneratedRegex(@"<PublishProfile>([^<]*\$\(Platform\)[^<]*\.pubxml)</PublishProfile>", RegexOptions.Singleline)]
    private static partial Regex PublishProfileElementRegex();

    public async Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
    {
        if (!csprojPath.Exists)
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(csprojPath.FullName, cancellationToken);
        var match = PublishProfileElementRegex().Match(content);

        if (!match.Success)
        {
            return false;
        }

        var profileValue = match.Groups[1].Value;
        var replacement = $"<PublishProfile Condition=\"Exists('Properties\\PublishProfiles\\{profileValue}')\">{profileValue}</PublishProfile>";
        content = content[..match.Index] + replacement + content[(match.Index + match.Length)..];

        await File.WriteAllTextAsync(csprojPath.FullName, content, cancellationToken);
        return true;
    }

    public async Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default)
    {
        var args = $"add \"{csprojPath.FullName}\" package \"{packageName}\"";
        if (version != null)
        {
            args += $" --version \"{version}\"";
        }
        else
        {
            args += " --prerelease";
        }
        var (exitCode, output, error) = await RunDotnetCommandAsync(csprojPath.Directory!, args, cancellationToken);

        if (exitCode != 0)
        {
            var message = !string.IsNullOrWhiteSpace(error) ? error.Trim() : output.Trim();
            throw new InvalidOperationException(
                $"Failed to add package {packageName} {version} (exit code {exitCode}): {message}");
        }

        // NOTE: This regex is tightly coupled to the current "dotnet add package" CLI output format.
        // If the dotnet team changes that message, this match may fail and we will fall back to
        // returning the requested version (if provided) or "latest" below.
        var pattern = $@"PackageReference for package '{Regex.Escape(packageName)}' version '([\d\.\-a-zA-Z]+)' (?:added to|updated in) file";
        var match = Regex.Match(output, pattern);
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }

        return version ?? "latest";
    }

    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public async Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default)
    {
        var packageList = await GetPackageListAsync(csprojPath, includeTransitive: false, cancellationToken);
        if (packageList?.Projects is null)
        {
            return false;
        }

        return packageList.Projects
            .SelectMany(p => p.Frameworks ?? [])
            .SelectMany(f => f.TopLevelPackages ?? [])
            .Any(pkg => string.Equals(pkg.Id, packageName, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, bool includeTransitive = true, CancellationToken cancellationToken = default)
    {
        if (!csprojFile.Exists)
        {
            return null;
        }

        var args = $"list \"{csprojFile.FullName}\" package{(includeTransitive ? " --include-transitive" : "")} --format json";
        var (exitCode, output, _) = await RunDotnetCommandAsync(csprojFile.Directory!, args, cancellationToken);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(output, DotNetServiceJsonContext.Default.DotNetPackageListJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(DotNetPackageListJson))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
internal partial class DotNetServiceJsonContext : JsonSerializerContext
{
}
