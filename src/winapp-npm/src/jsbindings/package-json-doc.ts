// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Shared helper for every wrapper site that reads or writes the user's
// `package.json`. Centralises:
//   * path-safety guard (workspace-rooted, UNC / reparse-point refusal);
//   * JSON parse + structural validation (root must be an object);
//   * EOL detection + trailing-newline preservation (matching the file's
//     existing convention rather than forcing LF on a CRLF checkout);
//   * atomic temp-file + rename, with copy+unlink fallback for cross-volume
//     edge cases (AV interference, mapped network shares, etc).
//
// Consumers (package-json-config.ts, runtime-dep-injector.ts) should NEVER
// open-code `fs.readFileSync(packageJson)` / `JSON.parse` / `fs.renameSync`
// — go through `readPackageJsonDoc` / `mutatePackageJsonDoc` so changes to
// safety policy land in one place.

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
 * Path-safety-guarded existence check. Returns false when:
 *   * package.json doesn't exist in the workspace,
 *   * OR the workspace itself / package.json path is UNC / reparse-backed
 *     (we treat unsafe paths as "not present" so callers fall through to
 *     their "no package.json" branch without leaking that we even probed).
 *
 * Use the boolean form when you just need to gate behaviour (`hasJsBindings`,
 * "should I trigger codegen?"). Use `readPackageJsonDoc` when you need the
 * parsed contents — that helper *throws* on safety violations.
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
 * Read + parse package.json. Returns `null` when the file does not exist.
 *
 * Throws when:
 *   * the workspace / file path is UNC or has a reparse-point ancestor
 *     (defence-in-depth against hostile workspace layouts);
 *   * the file is not valid JSON;
 *   * the top-level JSON value is not an object.
 *
 * Callers that need a "missing-or-malformed → silent fall-through" semantic
 * should call `packageJsonExists` first and then handle parse errors
 * themselves; the default behaviour here is "fail loud" so a malformed
 * package.json surfaces the real parse error instead of being silently
 * swallowed.
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
 * Read package.json, apply `mutate` to the parsed object, then atomically
 * write the result back. `mutate` may either:
 *   * mutate the object in place (returning `void`), or
 *   * return a new object (e.g. when reordering keys to preserve layout).
 *
 * Throws if package.json doesn't exist — callers that want a no-op when the
 * file is missing should branch on `packageJsonExists` first.
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
 * Atomically write `content` to `filePath`. Strategy:
 *   1. Write to a sibling temp file in the same directory (same-volume).
 *   2. `fsync` to flush kernel buffers to disk (best-effort — some FUSE /
 *      network filesystems don't implement it; ignore the error).
 *   3. `renameSync` over the destination. On NTFS / ext4 this is atomic at
 *      the directory-entry level: readers see either the old file or the new
 *      file, never a half-written one.
 *   4. On rename failure (cross-volume, AV interference, sharing violation),
 *      fall back to `copyFileSync` + `unlinkSync` — non-atomic, but at least
 *      we don't leave the destination empty.
 *
 * Exported so future workspace writers (other config files, lockfile-writer
 * mirrors, etc.) can use the same primitive instead of re-implementing.
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
      // Fallback: copy then unlink. Not atomic, but better than leaving the
      // destination empty or the user's package.json half-written.
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
