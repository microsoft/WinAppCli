// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Single chokepoint for package.json reads/writes across wrapper sites.
// Centralizes path-safety, object validation, EOL/trailing-newline preservation,
// and atomic sibling-temp writes with copy+unlink fallback for AV/cross-volume cases.
// Use these helpers so JS bindings and addon scaffolders share the same policy.

import * as fs from 'fs';
import * as path from 'path';
import { assertSafeWorkspaceFile } from './path-safety';

export const PACKAGE_JSON_FILENAME = 'package.json';

export interface PackageJsonDoc {
  /** Absolute path of the read file. */
  filePath: string;
  /** Parsed JSON object (top-level must be an object — arrays / scalars throw). */
  parsed: Record<string, unknown>;
  /** Original raw text — useful for downstream diff / unchanged checks. */
  raw: string;
  /** `'\r\n'` if the file uses CRLF anywhere, else `'\n'`. */
  eol: string;
  /** True if the original file ended with a newline; we preserve this on write. */
  trailingNewline: boolean;
}

/**
 * Path-safety-guarded existence check. Unsafe paths return false without probing;
 * use `readPackageJsonDoc` when safety violations should throw.
 */
export function packageJsonExists(workspaceDir: string): boolean {
  const filePath = path.join(workspaceDir, PACKAGE_JSON_FILENAME);
  try {
    assertSafeWorkspaceFile(workspaceDir, filePath, PACKAGE_JSON_FILENAME);
  } catch {
    return false;
  }
  return fs.existsSync(filePath);
}

/**
 * Read + parse package.json, returning `null` when absent. Throws on unsafe paths,
 * invalid JSON, or non-object roots so malformed package.json fails loud.
 */
export function readPackageJsonDoc(workspaceDir: string): PackageJsonDoc | null {
  const filePath = path.join(workspaceDir, PACKAGE_JSON_FILENAME);
  assertSafeWorkspaceFile(workspaceDir, filePath, PACKAGE_JSON_FILENAME);
  if (!fs.existsSync(filePath)) {
    return null;
  }

  let raw: string;
  try {
    raw = fs.readFileSync(filePath, 'utf8');
  } catch (err) {
    throw new Error(`Failed to read ${filePath}: ${(err as Error).message}`, { cause: err });
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(`Failed to parse ${filePath}: ${(err as Error).message}`, { cause: err });
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error(`Unexpected JSON shape in ${filePath}: top-level value must be an object.`);
  }

  return {
    filePath,
    parsed: parsed as Record<string, unknown>,
    raw,
    eol: raw.includes('\r\n') ? '\r\n' : '\n',
    trailingNewline: raw.endsWith('\n'),
  };
}

/**
 * Read package.json, apply `mutate`, then atomically write it back.
 * `mutate` may edit in place or return a replacement object; absence throws.
 */
export function mutatePackageJsonDoc(
  workspaceDir: string,
  mutate: (parsed: Record<string, unknown>) => void | Record<string, unknown>
): void {
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    throw new Error(
      `package.json not found in ${workspaceDir}. ` + 'Run `npm init -y` (or equivalent) before mutating package.json.'
    );
  }

  const result = mutate(doc.parsed);
  const next = result ?? doc.parsed;

  const serialized = JSON.stringify(next, null, 2).replace(/\n/g, doc.eol);
  const final = doc.trailingNewline && !serialized.endsWith(doc.eol) ? serialized + doc.eol : serialized;

  atomicWriteFile(doc.filePath, final);
}

/**
 * Atomic-ish write: sibling temp + best-effort fsync + rename, so readers see old or new.
 * If rename fails (cross-volume/AV/share), copy+unlink avoids emptying the target.
 */
export function atomicWriteFile(filePath: string, content: string): void {
  const dir = path.dirname(filePath);
  const tmpName = `.${path.basename(filePath)}.${process.pid}.${Date.now()}.${Math.random().toString(36).slice(2)}.tmp`;
  const tmpPath = path.join(dir, tmpName);

  let staged = false;
  try {
    const fd = fs.openSync(tmpPath, 'w');
    try {
      fs.writeFileSync(fd, content);
      try {
        fs.fsyncSync(fd);
      } catch {
        // fsync unsupported on some platforms (e.g., certain FUSE mounts on CI).
      }
    } finally {
      fs.closeSync(fd);
    }
    staged = true;

    fs.renameSync(tmpPath, filePath);
    staged = false;
    return;
  } catch (renameErr) {
    if (staged) {
      // Non-atomic fallback, but avoids empty or half-written package.json.
      try {
        fs.copyFileSync(tmpPath, filePath);
        try {
          fs.unlinkSync(tmpPath);
        } catch {
          /* leaked tmp is harmless */
        }
        return;
      } catch (fallbackErr) {
        try {
          fs.unlinkSync(tmpPath);
        } catch {
          /* ignore */
        }
        throw new Error(
          `Failed to write ${filePath}: ${(fallbackErr as Error).message} ` +
            `(after rename error: ${(renameErr as Error).message})`,
          { cause: fallbackErr }
        );
      }
    }
    throw new Error(`Failed to write ${filePath}: ${(renameErr as Error).message}`, { cause: renameErr });
  }
}
