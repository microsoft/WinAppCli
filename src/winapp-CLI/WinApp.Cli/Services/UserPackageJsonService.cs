// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal sealed class UserPackageJsonService : IUserPackageJsonService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public RuntimeDependencyOutcome EnsureRuntimeDependency(
        DirectoryInfo workspaceDirectory,
        string packageName,
        string version)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var packageJsonPath = Path.Combine(workspaceDirectory.FullName, "package.json");

        // Refuse symlinks/junctions and reparse-point ancestors BEFORE any
        // probe. `File.Exists` internally calls FindFirstFile which on a
        // reparse-point ancestor would silently follow the link (and on a
        // UNC ancestor would trigger SMB negotiation / NTLM leak) before
        // we got a chance to refuse. Run the non-probing string + attribute
        // check first.
        if (PathSafety.HasReparsePointOnPath(packageJsonPath, workspaceDirectory.FullName))
        {
            throw new InvalidOperationException(
                $"Refusing to rewrite '{packageJsonPath}': the file or one of its "
                + "ancestors is a symbolic link / reparse point. Resolve the link "
                + "and re-run, or add the runtime dependency manually.");
        }

        if (!File.Exists(packageJsonPath))
        {
            return RuntimeDependencyOutcome.NoPackageJson;
        }

        // JsonNode preserves unrelated keys exactly; JsonSerializer would
        // re-shape the whole file.
        JsonNode? root;
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            root = JsonNode.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse {packageJsonPath}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to read {packageJsonPath}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"No permission to read {packageJsonPath}: {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"{packageJsonPath} root is not a JSON object.");
        }

        if (obj["dependencies"] is JsonObject deps && deps[packageName] != null)
        {
            return RuntimeDependencyOutcome.AlreadyPresent;
        }

        // Don't auto-promote dev→dep; the user pinned it under dev for a reason.
        if (obj["devDependencies"] is JsonObject devDeps && devDeps[packageName] != null)
        {
            return RuntimeDependencyOutcome.PresentInDevDependencies;
        }

        if (obj["dependencies"] is not JsonObject deps2)
        {
            deps2 = new JsonObject();
            // Insert "dependencies" right after "version" (conventional layout).
            obj = ReinsertWithDependencies(obj, deps2);
            root = obj;
        }
        deps2[packageName] = JsonValue.Create(version);

        // 2-space indent matches npm/yarn/pnpm; relaxed escaping keeps '/' readable.
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var serialized = root.ToJsonString(options);

        // Preserve trailing newline if the original had one.
        try
        {
            var original = File.ReadAllText(packageJsonPath);
            if (original.EndsWith('\n') && !serialized.EndsWith('\n'))
            {
                serialized += '\n';
            }

            // Atomic write: stage to sibling temp + rename so a crash mid-write
            // cannot leave the user with an invalid / empty package.json.
            PathSafety.AtomicWriteAllText(packageJsonPath, serialized, Utf8NoBom);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to write {packageJsonPath}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"No permission to write {packageJsonPath}: {ex.Message}", ex);
        }
        return RuntimeDependencyOutcome.Added;
    }

    // Rebuild `original` with `newDependencies` slotted right after "version"
    // (or appended). JsonNode children can only have one parent, so we detach
    // and re-parent.
    private static JsonObject ReinsertWithDependencies(JsonObject original, JsonObject newDependencies)
    {
        var rebuilt = new JsonObject();
        bool inserted = false;

        var entries = original.ToList();
        foreach (var kvp in entries)
        {
            original.Remove(kvp.Key);
        }

        foreach (var (key, value) in entries)
        {
            rebuilt[key] = value;
            if (!inserted && string.Equals(key, "version", StringComparison.Ordinal))
            {
                rebuilt["dependencies"] = newDependencies;
                inserted = true;
            }
        }

        if (!inserted)
        {
            rebuilt["dependencies"] = newDependencies;
        }

        return rebuilt;
    }
}
