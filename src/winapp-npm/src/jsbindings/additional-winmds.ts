// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Resolves entries from `jsBindings.additionalWinmds` / `additionalRefs` to
// absolute file paths, with the same defenses as the original C# code:
//   * Reject UNC / network paths before any probe (FileInfo.Exists on a UNC
//     would trigger SMB negotiation and leak NTLM).
//   * Reject reparse-point ancestors (symlink/junction) — for absolute paths
//     under the workspace, boundary = workspace; for absolute paths outside
//     the workspace, boundary = the drive root.
//   * Silently skip missing files (codegen would just fail anyway).
//   * Dedupe by full path, case-insensitive.

import * as fs from 'fs';
import * as path from 'path';
import { isNetworkPath, hasReparsePointOnPath } from './path-safety';

export interface ResolveAdditionalWinmdsResult {
  resolved: string[];
  warnings: string[];
}

export function resolveAdditionalWinmds(
  entries: readonly string[] | undefined,
  workspaceDir: string,
  fieldName: string
): ResolveAdditionalWinmdsResult {
  const resolved: string[] = [];
  const warnings: string[] = [];
  if (!entries || entries.length === 0) {
    return { resolved, warnings };
  }

  const seen = new Set<string>();
  const workspaceFull = path.resolve(workspaceDir).replace(/[\\/]+$/, '');

  for (const entry of entries) {
    if (typeof entry !== 'string' || !entry.trim()) {
      continue;
    }
    const trimmed = entry.trim();

    // Reject UNC entries up-front (before any FS probe).
    if (isNetworkPath(trimmed)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — network/UNC paths are not allowed (would probe attacker-controlled host). Entry: ${entry}`
      );
      continue;
    }

    const fullPath = path.isAbsolute(trimmed) ? path.resolve(trimmed) : path.resolve(workspaceFull, trimmed);

    // Re-check after resolve: a relative path under a UNC workspace
    // resolves to a UNC.
    if (isNetworkPath(fullPath)) {
      warnings.push(
        `jsBindings.${fieldName} entry resolved to UNC path; refusing to probe. Entry: ${entry} → ${fullPath}`
      );
      continue;
    }

    // Reparse-point guard.
    //   * Relative paths and absolute paths under the workspace → boundary = workspace.
    //   * Absolute paths outside the workspace → boundary = drive root.
    //   The user explicitly opted in to an out-of-workspace path (docs
    //   support absolute paths); we still walk every segment for reparse
    //   points, but don't force workspace containment.
    const sameAsWorkspace = fullPath.toLowerCase() === workspaceFull.toLowerCase();
    const underWorkspace =
      sameAsWorkspace || fullPath.toLowerCase().startsWith((workspaceFull + path.sep).toLowerCase());
    const reparseBoundary = underWorkspace ? workspaceFull : path.parse(fullPath).root || workspaceFull;

    if (hasReparsePointOnPath(fullPath, reparseBoundary)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — file or one of its ancestors up to ${reparseBoundary} is a reparse point. Entry: ${entry} → ${fullPath}`
      );
      continue;
    }

    const dedupeKey = fullPath.toLowerCase();
    if (seen.has(dedupeKey)) {
      continue;
    }
    seen.add(dedupeKey);

    if (!fs.existsSync(fullPath)) {
      warnings.push(`jsBindings.${fieldName} entry not found, skipping: ${entry} (resolved to ${fullPath})`);
      continue;
    }

    resolved.push(fullPath);
  }

  return { resolved, warnings };
}

// Codegen extraTypes are silently skipped when malformed; count the valid
// entries for orchestration decisions (empty-emit + no valid extra types =
// nothing to do).
export function countValidExtraTypes(extraTypes: readonly JsBindingsExtraType[] | undefined): number {
  if (!extraTypes) {
    return 0;
  }
  let count = 0;
  for (const et of extraTypes) {
    if (et && et.namespace && et.namespace.trim() && et.classes && et.classes.length > 0) {
      count++;
    }
  }
  return count;
}

// Shape of one `extraTypes` entry in the JS bindings configuration block.
// The canonical schema lives in package-json-config.ts (the
// `"winapp.jsBindings"` namespace inside package.json); the type lives here
// to break a circular import between package-json-config.ts and
// additional-winmds.ts (the latter is used by codegen-runner.ts to expand
// `additionalWinmds` paths).
export interface JsBindingsExtraType {
  namespace: string;
  classes: string[];
}
