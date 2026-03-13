// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Converts MCP Bundle (.mcpb) files into MSIX-ready staging directories.
/// </summary>
internal sealed partial class McpbService : IMcpbService
{
    public async Task<McpbConversionResult> ExtractAndPrepareAsync(
        FileInfo mcpbPath,
        string architecture,
        string publisher,
        string? runtimePath,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcpb-extract-{Guid.NewGuid():N}");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"mcpb-staging-{Guid.NewGuid():N}");

        try
        {
            // Step 1: Extract MCPB (it's a ZIP archive)
            taskContext.AddDebugMessage($"{UiSymbols.Package} Extracting MCPB: {mcpbPath.FullName}");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(mcpbPath.FullName, tempDir);
            taskContext.AddDebugMessage($"{UiSymbols.Check} Extracted to temp directory");

            // Step 2: Parse and validate manifest.json
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("manifest.json not found in MCPB archive");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize(manifestJson, McpbJsonContext.Default.McpbManifest)
                ?? throw new InvalidOperationException("Failed to parse manifest.json");

            taskContext.AddDebugMessage($"{UiSymbols.Check} Parsed manifest.json");

            // Step 3: Validate
            var (resolvedRuntimePath, isScriptServer) = ValidateManifest(manifest, tempDir, runtimePath, taskContext);

            // Step 4: Stage files and generate AppxManifest
            Directory.CreateDirectory(stagingDir);
            var assetsDir = Path.Combine(stagingDir, "Assets");
            Directory.CreateDirectory(assetsDir);

            var packageName = SanitizePackageName(manifest.Name!);
            var packageVersion = ConvertToMsixVersion(manifest.Version!);
            var displayName = manifest.Name!;
            var description = manifest.Description ?? displayName;
            var publisherDisplayName = manifest.Author?.Name ?? "Unknown";
            var serverId = SanitizeServerId(manifest.Name!);
            var entryPointExe = manifest.Server!.EntryPoint!;
            var registrationFile = "manifest.json";

            // For script-based servers, the entry point is the runtime executable
            if (isScriptServer && resolvedRuntimePath != null)
            {
                entryPointExe = Path.GetFileName(resolvedRuntimePath);
            }

            taskContext.AddDebugMessage($"Package Name:    {packageName}");
            taskContext.AddDebugMessage($"Package Version: {packageVersion}");
            taskContext.AddDebugMessage($"Display Name:    {displayName}");
            taskContext.AddDebugMessage($"Architecture:    {architecture}");
            taskContext.AddDebugMessage($"Entry Point:     {entryPointExe}");

            // Generate capabilities XML
            var windowsMeta = manifest.GetWindowsMeta();
            var capabilitiesXml = GenerateCapabilitiesXml(windowsMeta?.Capabilities);

            // Generate AppxManifest.xml from template
            var template = await LoadMcpServerTemplateAsync(cancellationToken);
            var appxManifest = template
                .Replace("{PackageName}", packageName)
                .Replace("{PackageVersion}", packageVersion)
                .Replace("{Publisher}", publisher)
                .Replace("{PublisherDisplayName}", publisherDisplayName)
                .Replace("{Description}", description)
                .Replace("{DisplayName}", displayName)
                .Replace("{ServerId}", serverId)
                .Replace("{EntryPointExe}", entryPointExe)
                .Replace("{RegistrationFile}", registrationFile)
                .Replace("{ProcessorArchitecture}", architecture);

            // Insert capabilities or remove placeholder
            appxManifest = string.IsNullOrEmpty(capabilitiesXml)
                ? appxManifest.Replace("{Capabilities}", "")
                : appxManifest.Replace("{Capabilities}", capabilitiesXml + "\n");

            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, "AppxManifest.xml"),
                appxManifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            taskContext.AddDebugMessage($"{UiSymbols.Check} Generated AppxManifest.xml");

            // Copy MCP manifest to Assets (for ODR registration)
            File.Copy(manifestPath, Path.Combine(assetsDir, registrationFile));
            taskContext.AddDebugMessage($"{UiSymbols.Check} Copied manifest.json to Assets/");

            // Copy icons (custom or defaults)
            await StageIconsAsync(manifest, tempDir, assetsDir, taskContext, cancellationToken);

            // Copy all server files (excluding manifest.json) to staging root
            foreach (var entry in Directory.EnumerateFileSystemEntries(tempDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dest = Path.Combine(stagingDir, name);
                if (Directory.Exists(entry))
                {
                    CopyDirectory(entry, dest);
                }
                else
                {
                    File.Copy(entry, dest, overwrite: true);
                }
            }
            taskContext.AddDebugMessage($"{UiSymbols.Check} Copied server files to staging");

            // Bundle runtime executable for script-based servers
            if (isScriptServer && resolvedRuntimePath != null)
            {
                var runtimeDest = Path.Combine(stagingDir, Path.GetFileName(resolvedRuntimePath));
                File.Copy(resolvedRuntimePath, runtimeDest, overwrite: true);
                taskContext.AddDebugMessage($"{UiSymbols.Check} Bundled runtime: {Path.GetFileName(resolvedRuntimePath)}");
            }

            var stagingDirInfo = new DirectoryInfo(stagingDir);
            return new McpbConversionResult(stagingDirInfo, packageName, packageVersion, displayName, entryPointExe);
        }
        catch
        {
            // Clean up staging on failure; temp extraction always cleaned up in caller
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            throw;
        }
        finally
        {
            // Always clean up the extraction temp directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    #region Validation

    private static (string? resolvedRuntimePath, bool isScriptServer) ValidateManifest(
        McpbManifest manifest,
        string extractDir,
        string? runtimePath,
        TaskContext taskContext)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Required top-level fields
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("Missing required field: name");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("Missing required field: version");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            errors.Add("Missing required field: description");
        }

        if (manifest.Server is null)
        {
            errors.Add("Missing required field: server");
        }

        // Author
        if (manifest.Author?.Name is null)
        {
            warnings.Add("Missing author.name — will use 'Unknown' as publisher display name");
        }

        // Server type and runtime resolution
        var isScriptServer = false;
        string? resolvedRuntimePath = null;

        if (manifest.Server is not null)
        {
            var serverType = manifest.Server.Type;
            if (!string.IsNullOrEmpty(serverType) && !string.Equals(serverType, "binary", StringComparison.OrdinalIgnoreCase))
            {
                isScriptServer = true;
                if (!string.IsNullOrEmpty(runtimePath))
                {
                    if (!File.Exists(runtimePath))
                    {
                        errors.Add($"Specified runtime path not found: {runtimePath}");
                    }
                    else
                    {
                        resolvedRuntimePath = Path.GetFullPath(runtimePath);
                    }
                }
                else
                {
                    resolvedRuntimePath = FindRuntimeExecutable(serverType);
                    if (resolvedRuntimePath != null)
                    {
                        taskContext.AddDebugMessage($"Auto-detected runtime: {resolvedRuntimePath}");
                    }
                    else
                    {
                        errors.Add($"Server type '{serverType}' requires a runtime. Use --runtime-path to specify the path to the runtime executable (e.g., node.exe, python.exe).");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(manifest.Server.EntryPoint))
            {
                errors.Add("Missing server.entry_point");
            }
            else if (!isScriptServer)
            {
                var entryPointPath = Path.Combine(extractDir, manifest.Server.EntryPoint);
                if (!File.Exists(entryPointPath))
                {
                    errors.Add($"Entry point '{manifest.Server.EntryPoint}' not found in MCPB archive");
                }
            }
        }

        // _meta section (required for Windows ODR)
        var windowsMeta = manifest.GetWindowsMeta();
        if (windowsMeta?.StaticResponses is null)
        {
            errors.Add("Missing _meta.com.microsoft.windows.static_responses section (required for Windows ODR registration)");
        }
        else
        {
            if (windowsMeta.StaticResponses.Initialize is null || windowsMeta.StaticResponses.Initialize.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                errors.Add("Missing _meta.com.microsoft.windows.static_responses.initialize");
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} _meta.static_responses.initialize present");
            }

            if (windowsMeta.StaticResponses.ToolsList is null || windowsMeta.StaticResponses.ToolsList.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                errors.Add("Missing _meta.com.microsoft.windows.static_responses.tools/list");
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} _meta.static_responses.tools/list present");
            }
        }

        // Report
        foreach (var w in warnings)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} {w}");
        }

        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Error} {e}");
            }
            throw new InvalidOperationException(
                $"MCPB validation failed with {errors.Count} error(s):\n" + string.Join("\n", errors.Select(e => $"  - {e}")));
        }

        taskContext.AddDebugMessage($"{UiSymbols.Check} MCPB validation passed");
        return (resolvedRuntimePath, isScriptServer);
    }

    #endregion

    #region Template loading

    private static async Task<string> LoadMcpServerTemplateAsync(CancellationToken cancellationToken = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        var templateResName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".Templates.appxmanifest.mcpserver.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Embedded MCP server manifest template not found");

        await using var stream = asm.GetManifestResourceStream(templateResName)
            ?? throw new FileNotFoundException($"Template resource not found: {templateResName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Converts a version string to MSIX 4-part format (x.y.z.w).
    /// </summary>
    internal static string ConvertToMsixVersion(string version)
    {
        var parts = version.Split('.');
        var result = new string[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = i < parts.Length ? parts[i] : "0";
        }
        return string.Join(".", result);
    }

    /// <summary>
    /// Sanitizes a name for use as an MSIX package name (alphanumeric and dots only).
    /// </summary>
    internal static string SanitizePackageName(string name)
    {
        return InvalidPackageNameCharRegex().Replace(name, ".");
    }

    /// <summary>
    /// Sanitizes a name for use as an AppExtension Id (alphanumeric only).
    /// </summary>
    internal static string SanitizeServerId(string name)
    {
        return InvalidServerIdCharRegex().Replace(name, "");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9.]")]
    private static partial Regex InvalidPackageNameCharRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex InvalidServerIdCharRegex();

    /// <summary>
    /// Generates MSIX capabilities XML from an array of capability names.
    /// </summary>
    private static string GenerateCapabilitiesXml(string[]? capabilities)
    {
        if (capabilities is null || capabilities.Length == 0)
        {
            return string.Empty;
        }

        string[] standardCaps = ["internetClient", "internetClientServer", "privateNetworkClientServer"];
        string[] uapCaps = ["documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary", "removableStorage"];
        string[] rescapCaps = ["broadFileSystemAccess", "runFullTrust", "downloadsFolder"];

        var lines = new List<string>();
        foreach (var cap in capabilities)
        {
            if (string.Equals(cap, "runFullTrust", StringComparison.Ordinal))
            {
                continue; // already in template
            }

            if (standardCaps.Contains(cap))
            {
                lines.Add($"    <Capability Name=\"{cap}\" />");
            }
            else if (uapCaps.Contains(cap))
            {
                lines.Add($"    <uap:Capability Name=\"{cap}\" />");
            }
            else if (rescapCaps.Contains(cap))
            {
                lines.Add($"    <rescap:Capability Name=\"{cap}\" />");
            }
            else
            {
                lines.Add($"    <uap:Capability Name=\"{cap}\" />");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Auto-detect runtime executable for script-based servers.
    /// </summary>
    private static string? FindRuntimeExecutable(string serverType)
    {
        var candidates = new List<string>();

        if (serverType.Contains("node", StringComparison.OrdinalIgnoreCase))
        {
            // Check PATH first
            var pathNode = FindInPath("node.exe");
            if (pathNode != null)
            {
                candidates.Add(pathNode);
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"));
        }
        else if (serverType.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            var pathPython = FindInPath("python.exe");
            if (pathPython != null)
            {
                candidates.Add(pathPython);
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "python.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python", "python.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindInPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null)
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, exeName);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        return null;
    }

    /// <summary>
    /// Stages icons from the MCPB into the MSIX Assets directory.
    /// Falls back to embedded default icons.
    /// </summary>
    private static async Task StageIconsAsync(
        McpbManifest manifest,
        string extractDir,
        string assetsDir,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var iconTargets = new Dictionary<string, int>
        {
            ["Square44x44Logo.png"] = 44,
            ["Square150x150Logo.png"] = 150,
            ["StoreLogo.png"] = 50
        };

        var usedCustomIcons = false;

        if (manifest.Icons is { Length: > 0 })
        {
            // Use the icons array — find best size match for each MSIX icon slot
            foreach (var (targetName, targetSize) in iconTargets)
            {
                var bestPath = FindBestIcon(manifest.Icons, targetSize, extractDir);
                if (bestPath != null)
                {
                    File.Copy(bestPath, Path.Combine(assetsDir, targetName), overwrite: true);
                    usedCustomIcons = true;
                }
            }
        }
        else if (!string.IsNullOrEmpty(manifest.Icon))
        {
            // Single icon field — use for all MSIX icon slots
            var singleIconPath = Path.Combine(extractDir, manifest.Icon);
            if (File.Exists(singleIconPath))
            {
                foreach (var targetName in iconTargets.Keys)
                {
                    File.Copy(singleIconPath, Path.Combine(assetsDir, targetName), overwrite: true);
                }
                usedCustomIcons = true;
            }
            else
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Icon file '{manifest.Icon}' not found in MCPB archive — using defaults");
            }
        }

        if (!usedCustomIcons)
        {
            await CopyDefaultIconsAsync(assetsDir, iconTargets.Keys, cancellationToken);
            taskContext.AddDebugMessage($"{UiSymbols.Check} Copied default icons");
        }
        else
        {
            // Fill in any missing icons with defaults
            foreach (var targetName in iconTargets.Keys)
            {
                if (!File.Exists(Path.Combine(assetsDir, targetName)))
                {
                    await CopyDefaultIconAsync(assetsDir, targetName, cancellationToken);
                }
            }
            taskContext.AddDebugMessage($"{UiSymbols.Check} Staged custom icons from MCPB");
        }
    }

    private static string? FindBestIcon(McpbIcon[] icons, int targetSize, string extractDir)
    {
        string? bestPath = null;
        var bestDelta = int.MaxValue;

        foreach (var icon in icons)
        {
            if (string.IsNullOrEmpty(icon.Src) || string.IsNullOrEmpty(icon.Size))
            {
                continue;
            }

            var size = ParseIconSize(icon.Size);
            if (size <= 0)
            {
                continue;
            }

            var delta = Math.Abs(size - targetSize);
            var iconPath = Path.Combine(extractDir, icon.Src);
            if (delta < bestDelta && File.Exists(iconPath))
            {
                bestPath = iconPath;
                bestDelta = delta;
            }
        }

        return bestPath;
    }

    private static int ParseIconSize(string sizeStr)
    {
        // Parse "WxH" format (e.g., "44x44")
        var parts = sizeStr.Split('x', 'X');
        return parts.Length >= 1 && int.TryParse(parts[0], out var width) ? width : 0;
    }

    private static async Task CopyDefaultIconsAsync(string assetsDir, IEnumerable<string> iconNames, CancellationToken cancellationToken)
    {
        foreach (var name in iconNames)
        {
            await CopyDefaultIconAsync(assetsDir, name, cancellationToken);
        }
    }

    private static async Task CopyDefaultIconAsync(string assetsDir, string iconName, CancellationToken cancellationToken)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".msix_default_assets.{iconName}", StringComparison.OrdinalIgnoreCase));

        if (resName is null)
        {
            return;
        }

        await using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null)
        {
            return;
        }

        var targetPath = Path.Combine(assetsDir, iconName);
        await using var fs = File.Create(targetPath);
        await stream.CopyToAsync(fs, cancellationToken);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    #endregion
}
