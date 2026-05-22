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

/**
 * One entry in `winapp.jsBindings.additionalWinmds` (in package.json).
 *
 *   * `winmdPath` alone → bulk-emit the entire winmd
 *   * `winmdPath` + `namespace` + non-empty `classes` → cherry-pick: only
 *     emit the listed classes from the namespace (the winmd is loaded as
 *     ref-only so codegen can resolve its other types if needed).
 */
export interface AdditionalWinmd {
  winmdPath: string;
  namespace?: string;
  classes?: string[];
}

/** An `AdditionalWinmd` whose `winmdPath` has been resolved + safety-checked. */
export interface ResolvedAdditionalWinmd {
  /** Absolute path to a real, on-disk, non-UNC, non-reparse winmd file. */
  winmdPath: string;
  namespace?: string;
  classes?: string[];
}

export interface ResolveAdditionalWinmdsResult {
  resolved: ResolvedAdditionalWinmd[];
  warnings: string[];
}

export function resolveAdditionalWinmds(
  entries: readonly AdditionalWinmd[] | undefined,
  workspaceDir: string,
  fieldName: string
): ResolveAdditionalWinmdsResult {
  const resolved: ResolvedAdditionalWinmd[] = [];
  const warnings: string[] = [];
  if (!entries || entries.length === 0) {
    return { resolved, warnings };
  }

  const seen = new Set<string>();
  const workspaceFull = path.resolve(workspaceDir).replace(/[\\/]+$/, '');

  for (const entry of entries) {
    if (!entry || typeof entry.winmdPath !== 'string' || !entry.winmdPath.trim()) {
      continue;
    }
    const trimmed = entry.winmdPath.trim();

    if (isNetworkPath(trimmed)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — network/UNC paths are not allowed (would probe attacker-controlled host). Entry: ${trimmed}`
      );
      continue;
    }

    const fullPath = path.isAbsolute(trimmed) ? path.resolve(trimmed) : path.resolve(workspaceFull, trimmed);

    if (isNetworkPath(fullPath)) {
      warnings.push(
        `jsBindings.${fieldName} entry resolved to UNC path; refusing to probe. Entry: ${trimmed} → ${fullPath}`
      );
      continue;
    }

    const sameAsWorkspace = fullPath.toLowerCase() === workspaceFull.toLowerCase();
    const underWorkspace =
      sameAsWorkspace || fullPath.toLowerCase().startsWith((workspaceFull + path.sep).toLowerCase());
    const reparseBoundary = underWorkspace ? workspaceFull : path.parse(fullPath).root || workspaceFull;

    if (hasReparsePointOnPath(fullPath, reparseBoundary)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — file or one of its ancestors up to ${reparseBoundary} is a reparse point. Entry: ${trimmed} → ${fullPath}`
      );
      continue;
    }

    const dedupeKey = fullPath.toLowerCase();
    if (seen.has(dedupeKey)) {
      continue;
    }
    seen.add(dedupeKey);

    if (!fs.existsSync(fullPath)) {
      warnings.push(`jsBindings.${fieldName} entry not found, skipping: ${trimmed} (resolved to ${fullPath})`);
      continue;
    }

    const ns = typeof entry.namespace === 'string' ? entry.namespace.trim() : '';
    const classes = Array.isArray(entry.classes)
      ? entry.classes.map((c) => (typeof c === 'string' ? c.trim() : '')).filter((c) => c.length > 0)
      : [];

    const out: ResolvedAdditionalWinmd = { winmdPath: fullPath };
    if (ns && classes.length > 0) {
      out.namespace = ns;
      out.classes = classes;
    }
    resolved.push(out);
  }

  return { resolved, warnings };
}

/** True when the resolved entry is a cherry-pick (has both namespace + classes). */
export function isCherryPick(
  entry: ResolvedAdditionalWinmd
): entry is ResolvedAdditionalWinmd & { namespace: string; classes: string[] } {
  return typeof entry.namespace === 'string' && Array.isArray(entry.classes) && entry.classes.length > 0;
}
