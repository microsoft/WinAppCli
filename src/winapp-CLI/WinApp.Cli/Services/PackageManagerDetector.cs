// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text.Json;

namespace WinApp.Cli.Services;

internal sealed class PackageManagerDetector : IPackageManagerDetector
{
    public DetectedPackageManager Detect(DirectoryInfo workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        // Priority 1: Corepack `packageManager` field (e.g. "pnpm@9.2.0").
        var packageJson = Path.Combine(workspaceDirectory.FullName, "package.json");
        if (File.Exists(packageJson))
        {
            var fromCorepack = TryReadCorepackField(packageJson);
            if (fromCorepack != null)
            {
                return fromCorepack;
            }
        }

        // Priority 2: lockfile sniffing. pnpm/yarn/bun first because
        // package-lock.json is sometimes auto-created by tools in non-npm
        // workspaces.
        if (File.Exists(Path.Combine(workspaceDirectory.FullName, "pnpm-lock.yaml")))
        {
            return new DetectedPackageManager("pnpm", "pnpm install");
        }

        if (File.Exists(Path.Combine(workspaceDirectory.FullName, "yarn.lock")))
        {
            return new DetectedPackageManager("yarn", "yarn install");
        }

        if (File.Exists(Path.Combine(workspaceDirectory.FullName, "bun.lockb")) ||
            File.Exists(Path.Combine(workspaceDirectory.FullName, "bun.lock")))
        {
            return new DetectedPackageManager("bun", "bun install");
        }

        if (File.Exists(Path.Combine(workspaceDirectory.FullName, "package-lock.json")) ||
            File.Exists(Path.Combine(workspaceDirectory.FullName, "npm-shrinkwrap.json")))
        {
            return new DetectedPackageManager("npm", "npm install");
        }

        // Fallback.
        return new DetectedPackageManager("npm", "npm install");
    }

    private static DetectedPackageManager? TryReadCorepackField(string packageJsonPath)
    {
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("packageManager", out var prop) ||
                prop.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            // Format: "<name>@<version>" with optional "+sha" suffix.
            var atIndex = raw.IndexOf('@');
            var name = atIndex >= 0 ? raw[..atIndex] : raw;
            return name.Trim().ToLowerInvariant() switch
            {
                "npm" => new DetectedPackageManager("npm", "npm install"),
                "yarn" => new DetectedPackageManager("yarn", "yarn install"),
                "pnpm" => new DetectedPackageManager("pnpm", "pnpm install"),
                "bun" => new DetectedPackageManager("bun", "bun install"),
                _ => null, // Unknown PM declaration; fall through to lockfile sniffing.
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
