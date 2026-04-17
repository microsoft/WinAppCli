// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text.Json;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Template provider for C# / .NET templates using the dotnet new template engine.
/// Discovers templates dynamically by reading template.json from the installed .nupkg archive.
/// </summary>
internal class DotNetTemplateProvider(IDotNetService dotNetService) : ITemplateProvider
{
    private const string TemplatePackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";
    private const string TemplateConfigFileName = "template.json";
    private const string TemplateConfigPath = ".template.config/" + TemplateConfigFileName;

    // Cache templates after first discovery within a session
    private IReadOnlyList<TemplateInfo>? _cachedTemplates;

    /// <inheritdoc />
    public string Language => "C#";

    /// <inheritdoc />
    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        // dotnet new install without a version always fetches latest.
        // Re-running updates if a newer version is available.
        var tempDir = new DirectoryInfo(Path.GetTempPath());
        var (exitCode, _, error) = await dotNetService.RunDotnetCommandAsync(
            tempDir,
            $"new install {TemplatePackageId}",
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to install/update template package '{TemplatePackageId}': {error}");
        }

        // Invalidate cache after install/update
        _cachedTemplates = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemplateInfo>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTemplates is not null)
        {
            return _cachedTemplates;
        }

        var nupkgPath = FindInstalledNupkg();
        if (nupkgPath is null)
        {
            throw new InvalidOperationException(
                $"Template package '{TemplatePackageId}' is not installed. " +
                $"Run 'dotnet new install {TemplatePackageId}' first.");
        }

        var templates = await Task.Run(() => ReadTemplatesFromNupkg(nupkgPath), cancellationToken);
        _cachedTemplates = templates;
        return templates;
    }

    /// <inheritdoc />
    public async Task<(int ExitCode, string Output, string Error)> CreateAsync(
        string shortName,
        string name,
        DirectoryInfo? outputDir,
        FileInfo? projectFile,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyList<string>? extraArgs,
        CancellationToken cancellationToken = default)
    {
        var args = $"new {shortName} -n \"{name}\"";

        if (outputDir is not null)
        {
            args += $" -o \"{outputDir.FullName}\"";
        }

        if (projectFile is not null)
        {
            args += $" --project \"{projectFile.FullName}\"";
        }

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                args += $" --{key} \"{value}\"";
            }
        }

        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
            {
                // Quote args that contain spaces to prevent dotnet CLI from splitting them
                args += arg.Contains(' ') ? $" \"{arg}\"" : $" {arg}";
            }
        }

        var workingDir = outputDir ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        if (!workingDir.Exists)
        {
            workingDir = workingDir.Parent ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        }

        return await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
    }

    /// <summary>
    /// Finds the installed .nupkg for the template package in the template engine packages directory.
    /// Returns the latest version by parsing semantic versions from the file names.
    /// </summary>
    private static string? FindInstalledNupkg()
    {
        var templateEngineDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".templateengine", "packages");

        if (!Directory.Exists(templateEngineDir))
        {
            return null;
        }

        var prefix = $"{TemplatePackageId}.";
        var matchingFiles = Directory.GetFiles(templateEngineDir, $"{prefix}*.nupkg");

        if (matchingFiles.Length == 0)
        {
            return null;
        }

        // Parse version from filename and sort semantically
        // Filename format: Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.3-alpha.nupkg
        return matchingFiles
            .Select(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                var versionStr = fileName[prefix.Length..];
                var parsed = Version.TryParse(versionStr.Split('-')[0], out var version);
                return (Path: f, Version: parsed ? version! : new Version(0, 0, 0), Raw: versionStr);
            })
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Raw) // Pre-release tie-breaker
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reads all template.json files from a .nupkg archive and converts them to TemplateInfo objects.
    /// </summary>
    private static List<TemplateInfo> ReadTemplatesFromNupkg(string nupkgPath)
    {
        var templates = new List<TemplateInfo>();

        using var archive = ZipFile.OpenRead(nupkgPath);
        var templateEntries = archive.Entries
            .Where(e => e.FullName.EndsWith(TemplateConfigPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var entry in templateEntries)
        {
            using var stream = entry.Open();
            var templateJson = JsonSerializer.Deserialize(stream, TemplateJsonContext.Default.TemplateJson);

            if (templateJson?.ShortName is null || templateJson.Name is null)
            {
                continue;
            }

            var templateType = string.Equals(templateJson.Tags?.Type, "item", StringComparison.OrdinalIgnoreCase)
                ? TemplateType.Item
                : TemplateType.Project;

            var parameters = ExtractUserParameters(templateJson.Symbols);

            templates.Add(new TemplateInfo(
                Name: templateJson.Name,
                ShortName: templateJson.ShortName,
                Description: templateJson.Description ?? string.Empty,
                Type: templateType,
                Language: "C#",
                Parameters: parameters));
        }

        return templates;
    }

    /// <summary>
    /// Extracts user-facing parameters from the symbols dictionary.
    /// Only includes symbols with type "parameter" (excludes derived, generated, bind, computed).
    /// </summary>
    private static List<TemplateParameter> ExtractUserParameters(
        Dictionary<string, TemplateJsonSymbol>? symbols)
    {
        if (symbols is null)
        {
            return [];
        }

        var parameters = new List<TemplateParameter>();

        foreach (var (name, symbol) in symbols)
        {
            if (!string.Equals(symbol.Type, "parameter", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dataType = symbol.DataType?.ToLowerInvariant() switch
            {
                "choice" => TemplateParameterDataType.Choice,
                "bool" => TemplateParameterDataType.Bool,
                _ => TemplateParameterDataType.Text
            };

            var choices = symbol.Choices?
                .Where(c => c.Choice is not null)
                .Select(c => new TemplateChoice(c.Choice!, c.Description))
                .ToList();

            parameters.Add(new TemplateParameter(
                Name: name,
                Description: symbol.Description,
                DataType: dataType,
                DefaultValue: symbol.DefaultValue,
                Choices: choices));
        }

        return parameters;
    }
}
