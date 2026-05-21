// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Reads `.winapp/winmds.lock.json` written by the native CLI's `restore`.
// The lockfile is a pure NuGet winmd inventory keyed by package id; the
// emit/refOnly/skip classification lives in `winmd-policy.ts` and is
// applied at codegen time, not at lockfile time.
//
// Ported from C# `WinmdsLockfileService.TryReadAsync`. Schema version
// mismatches return null with a console hint so the caller can ask the
// user to re-run `winapp restore`.

import * as fs from 'fs';
import * as path from 'path';

// Schema bumped to 3 when the npm wrapper took over JS bindings: schema 2
// embedded a `category` field that is now strictly an npm-side computation.
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

export function getLockfilePath(winappDir: string): string {
  return path.join(winappDir, LOCKFILE_NAME);
}

export interface ReadLockfileResult {
  lockfile: WinmdsLockfile | null;
  /** Human-readable reason when lockfile is null but a file existed. */
  reason?: string;
}

// Reads + parses the lockfile, validating the schema version. Returns null
// when the file is missing, unreadable, malformed, or schema-mismatched —
// callers should treat any null as "trigger live discovery / ask the user
// to rerun restore".
export function tryReadLockfile(winappDir: string): ReadLockfileResult {
  const filePath = getLockfilePath(winappDir);
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
  // Native writes `schemaVersion`; tolerate the legacy `schema` key just in case.
  const schemaRaw = obj.schemaVersion ?? obj.schema;
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
    const winmdsArr = Array.isArray(e.winmds) ? e.winmds.filter((w): w is string => typeof w === 'string') : [];
    packages.push({ name, version, winmds: winmdsArr });
  }

  const lockfile: WinmdsLockfile = {
    schemaVersion,
    generatedAt: typeof obj.generatedAt === 'string' ? obj.generatedAt : undefined,
    nugetCacheDir: typeof obj.nugetCacheDir === 'string' ? obj.nugetCacheDir : undefined,
    yamlPackagesHash: typeof obj.yamlPackagesHash === 'string' ? obj.yamlPackagesHash : undefined,
    packages,
  };
  return { lockfile };
}
