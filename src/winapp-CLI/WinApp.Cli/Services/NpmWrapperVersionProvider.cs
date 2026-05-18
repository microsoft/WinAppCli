// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text.Json;

namespace WinApp.Cli.Services;

// Walks up from winapp.exe to the wrapper's package.json (name =
// "@microsoft/winappcli") and reads the dynwinrt-codegen dep pin. Only
// dynwinrt-codegen is in dependencies; dynwinrt shares the same pin.
internal sealed class NpmWrapperVersionProvider : INpmWrapperVersionProvider
{
    private const string WrapperPackageName = "@microsoft/winappcli";
    private const string DynWinrtCodegenPackageName = "@microsoft/dynwinrt-codegen";

    private readonly Lazy<string> _version;

    public NpmWrapperVersionProvider()
    {
        _version = new Lazy<string>(Locate);
    }

    public string DynWinrtVersion => _version.Value;

    public string DynWinrtCodegenVersion => _version.Value;

    private static string Locate()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            throw new InvalidOperationException(
                "Environment.ProcessPath is empty. Cannot locate the @microsoft/winappcli npm wrapper. " +
                "If you reached this from a test or `dotnet run`, register a stub " +
                "INpmWrapperVersionProvider in DI.");
        }

        return LocateFrom(Path.GetDirectoryName(exePath)!);
    }

    // Internal seam so tests can drive a synthetic layout without
    // shelling through Environment.ProcessPath.
    internal static string LocateFrom(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "package.json");
            if (File.Exists(candidate))
            {
                if (TryReadVersion(candidate, out var version))
                {
                    return version;
                }
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the {WrapperPackageName} package.json near {startDirectory}. " +
            $"This typically means winapp.exe is running outside its npm install layout " +
            $"(e.g. `dotnet run` during local development). Register a stub " +
            $"INpmWrapperVersionProvider in DI for that scenario.");
    }

    private static bool TryReadVersion(string packageJsonPath, out string version)
    {
        version = string.Empty;
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String ||
                !string.Equals(nameProp.GetString(), WrapperPackageName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("dependencies", out var deps) ||
                deps.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"{packageJsonPath} is the {WrapperPackageName} package.json but has no 'dependencies' object.");
            }

            version = ReadDep(deps, DynWinrtCodegenPackageName, packageJsonPath);
            return true;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse {packageJsonPath}: {ex.Message}", ex);
        }
    }

    private static string ReadDep(JsonElement deps, string packageName, string packageJsonPath)
    {
        if (!deps.TryGetProperty(packageName, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"{packageJsonPath} is missing '{packageName}' in its 'dependencies'. " +
                $"This indicates a build issue with the @microsoft/winappcli npm package.");
        }

        var version = prop.GetString();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(
                $"{packageJsonPath} declares '{packageName}' with an empty version.");
        }

        return version;
    }
}

