// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Reads the native restore lockfile; winmd-policy.ts owns emit/refOnly/skip.

import * as fs from 'fs';
import * as path from 'path';
import { assertSafeWorkspaceFile, isNetworkPath, hasReparsePointOnPath } from './path-safety';

// Schema 3 dropped native-side JS binding categories; npm computes them now.
export const LOCKFILE_SCHEMA_VERSION = 3;
export const LOCKFILE_NAME = 'winmds.lock.json';

export interface WinmdsLockfilePackage {
  name: string;
  version: string;
  winmds: string[];
}

export interface WinmdsLockfile {
  schemaVersion: number;
  generatedAt?: string;
  nugetCacheDir?: string;
  yamlPackagesHash?: string;
  packages: WinmdsLockfilePackage[];
}

/** Exposed so cli.ts can probe without parsing the lockfile. */
export function getLockfilePath(workspaceDir: string): string {
  return path.join(workspaceDir, '.winapp', LOCKFILE_NAME);
}

export function lockfileExists(workspaceDir: string): boolean {
  const filePath = getLockfilePath(workspaceDir);
  assertSafeWorkspaceFile(workspaceDir, filePath, LOCKFILE_NAME);
  return fs.existsSync(filePath);
}

export interface ReadLockfileResult {
  lockfile: WinmdsLockfile | null;
  /** Human-readable reason when null and a file existed or was unsafe. */
  reason?: string;
}

/** Read and validate the lockfile, rejecting hostile `.winapp` junctions before open. */
export function tryReadLockfile(workspaceDir: string): ReadLockfileResult {
  const winappDir = path.join(workspaceDir, '.winapp');
  const filePath = getLockfilePath(workspaceDir);

  // Refuse reparse/UNC ancestors before probing existence.
  try {
    assertSafeWorkspaceFile(workspaceDir, filePath, LOCKFILE_NAME);
  } catch (err) {
    return { lockfile: null, reason: (err as Error).message };
  }

  if (!fs.existsSync(filePath)) {
    return { lockfile: null };
  }

  let raw: string;
  try {
    raw = fs.readFileSync(filePath, 'utf8');
  } catch (err) {
    return {
      lockfile: null,
      reason: `Failed to read ${filePath}: ${(err as Error).message}`,
    };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    return {
      lockfile: null,
      reason: `Lockfile ${filePath} is not valid JSON: ${(err as Error).message}`,
    };
  }

  if (!parsed || typeof parsed !== 'object') {
    return {
      lockfile: null,
      reason: `Lockfile ${filePath} root is not an object`,
    };
  }

  const obj = parsed as Record<string, unknown>;
  // Native writes snake_case; camelCase remains a legacy/test fallback.
  const schemaRaw = obj.schema ?? obj.schemaVersion;
  const schemaVersion =
    typeof schemaRaw === 'number' ? schemaRaw : typeof schemaRaw === 'string' ? Number(schemaRaw) : Number.NaN;

  if (!Number.isFinite(schemaVersion) || schemaVersion !== LOCKFILE_SCHEMA_VERSION) {
    return {
      lockfile: null,
      reason:
        `Lockfile ${filePath} schema mismatch (got ${schemaRaw}, expected ${LOCKFILE_SCHEMA_VERSION}). ` +
        `Re-run \`winapp restore\` to regenerate.`,
    };
  }

  const packagesRaw = obj.packages;
  if (!Array.isArray(packagesRaw)) {
    return {
      lockfile: null,
      reason: `Lockfile ${filePath} has no 'packages' array`,
    };
  }

  const generatedAt =
    typeof obj.generated_at === 'string'
      ? obj.generated_at
      : typeof obj.generatedAt === 'string'
        ? obj.generatedAt
        : undefined;
  const nugetCacheDir =
    typeof obj.nuget_cache_dir === 'string'
      ? obj.nuget_cache_dir
      : typeof obj.nugetCacheDir === 'string'
        ? obj.nugetCacheDir
        : undefined;
  if (!nugetCacheDir || !nugetCacheDir.trim()) {
    return {
      lockfile: null,
      reason:
        `Lockfile ${filePath} missing nuget_cache_dir. ` +
        `Re-run \`winapp restore\` to regenerate (older lockfiles without a containment boundary are unsafe).`,
    };
  }
  const yamlPackagesHash =
    typeof obj.yaml_packages_hash === 'string'
      ? obj.yaml_packages_hash
      : typeof obj.yamlPackagesHash === 'string'
        ? obj.yamlPackagesHash
        : undefined;

  const cacheBoundary = path.resolve(nugetCacheDir).replace(/[\\/]+$/, '');

  // Reject filesystem roots — a tampered lockfile setting nuget_cache_dir to a
  // drive root (e.g. "C:\") would let every absolute path under that drive
  // pass containment. Use `path.dirname(x) === x` as the canonical root test
  // so legitimate shallow caches like "D:\packages" or "C:\nuget" (common when
  // NUGET_PACKAGES is customized off the user profile) still pass. UNC roots
  // are handled separately by isNetworkPath.
  const isFsRoot = path.dirname(cacheBoundary) === cacheBoundary;
  if (isFsRoot || isNetworkPath(cacheBoundary)) {
    return {
      lockfile: null,
      reason:
        `Lockfile ${filePath} contains a suspiciously broad nuget_cache_dir ("${nugetCacheDir}"). ` +
        `Re-run \`winapp restore\` to regenerate with a valid NuGet cache path.`,
    };
  }
  const droppedPaths: string[] = [];
  const packages: WinmdsLockfilePackage[] = [];
  for (const entry of packagesRaw) {
    if (!entry || typeof entry !== 'object') {
      continue;
    }
    const e = entry as Record<string, unknown>;
    const name = typeof e.name === 'string' ? e.name : null;
    const version = typeof e.version === 'string' ? e.version : null;
    if (!name || !version) {
      continue;
    }
    const rawWinmds = Array.isArray(e.winmds) ? e.winmds.filter((w): w is string => typeof w === 'string') : [];
    const winmdsArr: string[] = [];
    for (const w of rawWinmds) {
      if (!w || !w.trim() || isNetworkPath(w)) {
        droppedPaths.push(w);
        continue;
      }
      const resolved = path.resolve(w);
      const prefix = cacheBoundary + path.sep;
      const inside = resolved.length >= prefix.length && resolved.toLowerCase().startsWith(prefix.toLowerCase());
      if (!inside) {
        droppedPaths.push(w);
        continue;
      }
      if (hasReparsePointOnPath(resolved, cacheBoundary)) {
        droppedPaths.push(w);
        continue;
      }
      let stat: fs.Stats;
      try {
        stat = fs.lstatSync(resolved);
      } catch {
        droppedPaths.push(w);
        continue;
      }
      if (!stat.isFile()) {
        droppedPaths.push(w);
        continue;
      }
      winmdsArr.push(resolved);
    }
    packages.push({ name, version, winmds: winmdsArr });
  }

  if (droppedPaths.length > 0) {
    // Include examples so tampered lockfiles are actionable.
    const head = droppedPaths.slice(0, 3).join(', ');
    const suffix = droppedPaths.length > 3 ? ` (+${droppedPaths.length - 3} more)` : '';
    return {
      lockfile: null,
      reason:
        `Lockfile ${filePath} contains ${droppedPaths.length} winmd path(s) outside the recorded ` +
        `nuget_cache_dir, missing, not files, or via UNC / reparse points: ${head}${suffix}. ` +
        'Re-run `winapp restore` to regenerate from a trusted NuGet cache.',
    };
  }

  // Best-effort guard against `.winapp` swaps after the initial probe.
  try {
    assertSafeWorkspaceFile(workspaceDir, winappDir, '.winapp');
  } catch (err) {
    return { lockfile: null, reason: (err as Error).message };
  }

  const lockfile: WinmdsLockfile = {
    schemaVersion,
    generatedAt,
    nugetCacheDir,
    yamlPackagesHash,
    packages,
  };
  return { lockfile };
}
