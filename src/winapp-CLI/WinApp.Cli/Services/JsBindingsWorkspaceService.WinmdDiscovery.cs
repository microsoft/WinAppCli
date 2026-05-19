// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// User-supplied winmd path validation (UNC guard, existence, dedupe)
// and live-mode NuGet transitive-dependency expansion.
internal sealed partial class JsBindingsWorkspaceService
{
    private List<FileInfo> ResolveAdditionalWinmds(
        List<string> entries,
        DirectoryInfo workspaceDir,
        TaskContext taskContext,
        string fieldName)
    {
        var resolved = new List<FileInfo>();
        if (entries is null || entries.Count == 0)
        {
            return resolved;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            var trimmed = entry.Trim();

            // Reject UNC / network paths before any probe — FileInfo.Exists
            // on a UNC triggers SMB negotiation and would leak the user's
            // NTLM hash to the remote host.
            if (PathSafety.IsNetworkPath(trimmed))
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Warning} {fieldName} entry rejected as network/UNC path (refusing to probe): {entry}");
                logger.LogWarning(
                    "{UISymbol} jsBindings.{FieldName} entry refused — network/UNC paths are not allowed (would probe attacker-controlled host on FileInfo.Exists). Entry: {Entry}",
                    UiSymbols.Warning,
                    fieldName,
                    entry);
                continue;
            }

            var fullPath = Path.IsPathFullyQualified(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(workspaceDir.FullName, trimmed));

            // Re-check after GetFullPath: a relative path under a UNC
            // workspaceDir resolves to a UNC.
            if (PathSafety.IsNetworkPath(fullPath))
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Warning} {fieldName} entry resolved to a network/UNC path, rejected: {entry} → {fullPath}");
                logger.LogWarning(
                    "{UISymbol} jsBindings.{FieldName} entry resolved to UNC path; refusing to probe. Entry: {Entry} → {FullPath}",
                    UiSymbols.Warning,
                    fieldName,
                    entry,
                    fullPath);
                continue;
            }

            // Reparse-point guard: walk down from a boundary to fullPath
            // and reject if any segment is a symlink/junction. Boundary
            // selection:
            //   * Relative paths and absolute paths under the workspace —
            //     boundary = workspaceDir. Workspace containment is the
            //     natural trust scope.
            //   * Absolute paths outside the workspace — boundary = drive
            //     root (e.g. `C:\`). The user explicitly opted in to an
            //     out-of-workspace path (docs/js-bindings.md says absolute
            //     paths are supported); we still walk every segment for
            //     reparse points, but we don't force workspace containment.
            //   PathSafety.HasReparsePointOnPath already handles drive-root
            //   boundary correctly (see DriveRootBoundary_StillRejectsJunctionDescendant).
            var underWorkspace = string.Equals(fullPath, workspaceDir.FullName, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    workspaceDir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            var reparseBoundary = underWorkspace
                ? workspaceDir.FullName
                : (Path.GetPathRoot(fullPath) ?? workspaceDir.FullName);
            if (PathSafety.HasReparsePointOnPath(fullPath, reparseBoundary))
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Warning} {fieldName} entry rejected — file or ancestor is a symlink/junction: {entry} → {fullPath}");
                logger.LogWarning(
                    "{UISymbol} jsBindings.{FieldName} entry refused — file or one of its ancestors up to {Boundary} is a reparse point. Entry: {Entry} → {FullPath}",
                    UiSymbols.Warning,
                    fieldName,
                    reparseBoundary,
                    entry,
                    fullPath);
                continue;
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            var fi = new FileInfo(fullPath);
            if (!fi.Exists)
            {
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} {fieldName} entry not found, skipping: {entry}");
                logger.LogWarning(
                    "{UISymbol} jsBindings.{FieldName} entry not found, skipping: {Entry} (resolved to {FullPath})",
                    UiSymbols.Note,
                    fieldName,
                    entry,
                    fullPath);
                continue;
            }

            resolved.Add(fi);
        }

        return resolved;
    }

    // Count extraTypes entries codegen would actually process; entries with
    // a blank namespace or no classes are silently skipped.
    internal static int CountValidExtraTypes(IReadOnlyList<JsBindingsExtraType> extraTypes)
    {
        if (extraTypes is null)
        {
            return 0;
        }
        var count = 0;
        foreach (var et in extraTypes)
        {
            if (!string.IsNullOrWhiteSpace(et.Namespace) && et.Classes.Count > 0)
            {
                count++;
            }
        }
        return count;
    }

    // (UNC / network-path detector lives on PathSafety so the reparse-point
    // guard and winmd discovery share the same definition — see
    // PathSafety.IsNetworkPath.)

    internal async Task<Dictionary<string, string>> ExpandTransitiveDependenciesAsync(
        Dictionary<string, string> usedVersions,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var expanded = new Dictionary<string, string>(usedVersions, StringComparer.OrdinalIgnoreCase);
        var roots = usedVersions.ToList();
        var failures = new List<(string Package, string Version, string Reason)>();
        foreach (var (name, version) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var deps = await nugetService.GetPackageDependenciesAsync(name, version, cancellationToken);
                foreach (var (depId, depVersionSpec) in deps)
                {
                    var depVersion = NugetService.ParseMinimumVersion(depVersionSpec);
                    if (string.IsNullOrEmpty(depVersion))
                    {
                        continue;
                    }
                    if (!expanded.ContainsKey(depId))
                    {
                        expanded[depId] = depVersion;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // User cancellation must propagate — never swallow.
                throw;
            }
            catch (Exception ex)
            {
                // Record but keep going; surface a single warning at the
                // end so users know bindings may be incomplete.
                failures.Add((name, version, ex.Message));
                taskContext.AddDebugMessage(
                    $"{UiSymbols.Note} Could not expand transitive deps for {name} {version}: {ex.Message}");
                logger.LogDebug(ex,
                    "Transitive dependency expansion failed for {PackageName} {Version}", name, version);
            }
        }

        if (failures.Count > 0)
        {
            var summary = string.Join(", ",
                failures.Take(5).Select(f => $"{f.Package} {f.Version}"));
            var ellipsis = failures.Count > 5 ? $" (+ {failures.Count - 5} more)" : string.Empty;
            logger.LogWarning(
                "{UISymbol} Could not resolve transitive NuGet dependencies for {Count} package(s): {Packages}{Ellipsis}. "
                + "Generated bindings may be incomplete (missing referenced types). "
                + "Run `winapp restore` to materialize the full dependency graph and try again.",
                UiSymbols.Warning,
                failures.Count,
                summary,
                ellipsis);
            taskContext.AddDebugMessage(
                $"{UiSymbols.Warning} Transitive expansion failures ({failures.Count}):");
            foreach (var f in failures)
            {
                taskContext.AddDebugMessage($"  - {f.Package} {f.Version}: {f.Reason}");
            }
        }

        return expanded;
    }
}
