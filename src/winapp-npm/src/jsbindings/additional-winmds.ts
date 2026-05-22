// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Reject UNC paths before probing to avoid SMB/NTLM leakage.
// Reject reparse-point ancestors; workspace paths use workspace as boundary,
// and absolute paths outside the workspace use the drive root.

import * as fs from 'fs';
import * as path from 'path';
import { isNetworkPath, hasReparsePointOnPath } from './path-safety';

/**
 * Package.json entry; `winmdPath` alone bulk-emits,
 * while namespace+classes cherry-picks.
 */
export interface AdditionalWinmd {
  winmdPath?: string;
  namespace?: string;
  classes?: string[];
}

export interface ResolvedAdditionalWinmd {
  /** Absolute path after UNC/reparse checks; undefined for auto-detect entries. */
  winmdPath?: string;
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

  const seenIndex = new Map<string, number>();
  const workspaceFull = path.resolve(workspaceDir).replace(/[\\/]+$/, '');

  for (const entry of entries) {
    if (!entry) {
      continue;
    }

    const rawPath = typeof entry.winmdPath === 'string' ? entry.winmdPath.trim() : '';
    const ns = typeof entry.namespace === 'string' ? entry.namespace.trim() : '';
    const classes = Array.isArray(entry.classes)
      ? entry.classes.map((c) => (typeof c === 'string' ? c.trim() : '')).filter((c) => c.length > 0)
      : [];

    // Path-less entry: rely on dynwinrt-codegen auto-detect (Windows.winmd in
    // the Windows SDK) — requires namespace+classes to be useful, otherwise
    // the entry has no actionable content.
    if (!rawPath) {
      if (!ns || classes.length === 0) {
        warnings.push(
          `jsBindings.${fieldName} entry has no winmdPath and no namespace+classes — skipping (nothing to generate).`
        );
        continue;
      }
      const dedupeKey = `<auto>|${ns}`;
      const existingIdx = seenIndex.get(dedupeKey);
      if (existingIdx !== undefined) {
        const existing = resolved[existingIdx];
        const merged = new Set<string>(existing.classes ?? []);
        for (const c of classes) {
          merged.add(c);
        }
        existing.namespace = ns;
        existing.classes = [...merged];
        continue;
      }
      seenIndex.set(dedupeKey, resolved.length);
      resolved.push({ namespace: ns, classes });
      continue;
    }

    if (isNetworkPath(rawPath)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — network/UNC paths are not allowed (would probe attacker-controlled host). Entry: ${rawPath}`
      );
      continue;
    }

    const fullPath = path.isAbsolute(rawPath) ? path.resolve(rawPath) : path.resolve(workspaceFull, rawPath);

    if (isNetworkPath(fullPath)) {
      warnings.push(
        `jsBindings.${fieldName} entry resolved to UNC path; refusing to probe. Entry: ${rawPath} → ${fullPath}`
      );
      continue;
    }

    const sameAsWorkspace = fullPath.toLowerCase() === workspaceFull.toLowerCase();
    const underWorkspace =
      sameAsWorkspace || fullPath.toLowerCase().startsWith((workspaceFull + path.sep).toLowerCase());
    const reparseBoundary = underWorkspace ? workspaceFull : path.parse(fullPath).root || workspaceFull;

    if (hasReparsePointOnPath(fullPath, reparseBoundary)) {
      warnings.push(
        `jsBindings.${fieldName} entry refused — file or one of its ancestors up to ${reparseBoundary} is a reparse point. Entry: ${rawPath} → ${fullPath}`
      );
      continue;
    }

    if (!fs.existsSync(fullPath)) {
      warnings.push(`jsBindings.${fieldName} entry not found, skipping: ${rawPath} (resolved to ${fullPath})`);
      continue;
    }

    const dedupeKey = `${fullPath.toLowerCase()}|${ns}`;
    const existingIdx = seenIndex.get(dedupeKey);
    if (existingIdx !== undefined) {
      const existing = resolved[existingIdx];
      if (ns && classes.length > 0) {
        const merged = new Set<string>(existing.classes ?? []);
        for (const c of classes) {
          merged.add(c);
        }
        existing.namespace = ns;
        existing.classes = [...merged];
      }
      continue;
    }
    seenIndex.set(dedupeKey, resolved.length);

    const out: ResolvedAdditionalWinmd = { winmdPath: fullPath };
    if (ns && classes.length > 0) {
      out.namespace = ns;
      out.classes = classes;
    }
    resolved.push(out);
  }

  return { resolved, warnings };
}

export function isCherryPick(
  entry: ResolvedAdditionalWinmd
): entry is ResolvedAdditionalWinmd & { namespace: string; classes: string[] } {
  return typeof entry.namespace === 'string' && Array.isArray(entry.classes) && entry.classes.length > 0;
}
