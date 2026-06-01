// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Filesystem-safety helpers ported from the C# `PathSafety` helper. Used by
// every site that writes into the user's workspace (package.json, winapp.yaml,
// codegen output) and by additional-winmds resolution.
//
// Invariants matching the C# original:
//   * `isNetworkPath` rejects UNC / `\\?\UNC\…` / `\\.\UNC\…`. Local DOS device
//     paths (`\\?\C:\…`) are NOT network.
//   * `hasReparsePointOnPath` walks DOWN from `boundary` to `path`. Walking up
//     would force the OS to traverse junctions/symlinks in `path` to read the
//     leaf's attributes, which on Windows would trigger SMB negotiation /
//     NTLM leak before we ever saw the reparse-point flag.

import * as fs from 'fs';
import * as path from 'path';

// True for UNC / network paths (`\\server\share`, `\\?\UNC\…`, `\\.\UNC\…`).
// Local DOS device paths (`\\?\C:\…`, `\\.\C:\…`) are not network.
export function isNetworkPath(p: string): boolean {
  if (!p) {
    return false;
  }
  // Normalize forward slashes — Windows accepts both.
  const norm = p.replace(/\//g, '\\');

  if (!norm.startsWith('\\\\')) {
    return false;
  }

  // Plain UNC: `\\server\share\…`
  if (!norm.startsWith('\\\\?\\') && !norm.startsWith('\\\\.\\')) {
    return true;
  }

  // DOS device namespace: `\\?\UNC\…` or `\\.\UNC\…` is network; other
  // device paths (drive letters) are not.
  const afterPrefix = norm.substring(4);
  return /^UNC\\/i.test(afterPrefix);
}

// True if `targetPath` is not safely contained under `boundary`, or if any
// segment from `boundary` down to `targetPath` is a reparse point, or if
// either side is a UNC path. Used to refuse rewriting files that a hostile
// workspace could redirect via a symlink/junction to a victim location.
export function hasReparsePointOnPath(targetPath: string, boundary: string): boolean {
  if (!targetPath || !boundary) {
    return false;
  }
  if (isNetworkPath(targetPath) || isNetworkPath(boundary)) {
    return true;
  }

  // Resolve both paths to absolute, normalized form for containment check.
  let absTarget: string;
  let absBoundary: string;
  try {
    absTarget = path.resolve(targetPath);
    absBoundary = path.resolve(boundary).replace(/[\\/]+$/, '');
  } catch {
    // If we can't even resolve the paths, treat as unsafe — safer default.
    return true;
  }

  const sep = path.sep;
  const boundaryWithSep = absBoundary + sep;

  // Containment: target must equal boundary OR be a descendant.
  const sameAsBoundary = absTarget.toLowerCase() === absBoundary.toLowerCase();
  const insideBoundary = absTarget.toLowerCase().startsWith(boundaryWithSep.toLowerCase());
  if (!sameAsBoundary && !insideBoundary) {
    return true;
  }

  // Walk DOWN from boundary to target, checking each existing segment's
  // attributes via lstat (does NOT follow symlinks).
  const rel = sameAsBoundary ? '' : absTarget.substring(boundaryWithSep.length);
  const segments = rel.length === 0 ? [] : rel.split(/[\\/]/).filter((s) => s.length > 0);

  let probe = absBoundary;
  if (isReparseSegment(probe)) {
    return true;
  }
  for (const seg of segments) {
    probe = path.join(probe, seg);
    if (isReparseSegment(probe)) {
      return true;
    }
  }
  return false;
}

function isReparseSegment(p: string): boolean {
  let stat: fs.Stats;
  try {
    stat = fs.lstatSync(p);
  } catch (err) {
    // Missing leaf is fine — caller will create it. Permission denied /
    // other unexpected error: treat as safe so we don't refuse the whole
    // workspace; the subsequent write will surface the real error.
    const code = (err as NodeJS.ErrnoException).code;
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      return false;
    }
    return false;
  }
  return stat.isSymbolicLink();
}

/**
 * Throw if `filePath` (or any segment from `workspaceDir` down to it) is a
 * reparse point or UNC path. Single chokepoint for every file we read or
 * write inside the user's workspace (`package.json`, `winapp.yaml`,
 * `.winapp/winmds.lock.json`, codegen output). Mirrors the native side's
 * `IsLockfilePathUnsafe()` / `PathSafety.AssertSafeWrite`.
 *
 * `label` is woven into the error message so the user can tell which file
 * tripped the guard (`'package.json'`, `'winmds.lock.json'`, …).
 */
export function assertSafeWorkspaceFile(workspaceDir: string, filePath: string, label: string): void {
  if (isNetworkPath(workspaceDir) || isNetworkPath(filePath)) {
    throw new Error(
      `Refusing to access ${label} at '${filePath}': workspace or target path is a UNC / network path. ` +
        'Use a local drive-letter path.'
    );
  }
  if (hasReparsePointOnPath(filePath, workspaceDir)) {
    throw new Error(
      `Refusing to access ${label} at '${filePath}': the file or one of its ` +
        `ancestors below '${workspaceDir}' is a reparse point (symlink / junction) ` +
        'or the file is outside the workspace. Resolve the link and re-run.'
    );
  }
}

/**
 * Stricter variant for directories that the wrapper will RECURSIVELY DELETE
 * before each run (e.g. dynwinrt-codegen output). Requires:
 *   * `outputDir` is strictly *inside* the workspace (not equal to it);
 *   * neither end of the path is UNC / network;
 *   * no segment from workspace down to outputDir is a reparse point;
 *   * if `outputDir` already exists, it is itself not a reparse point.
 *
 * Throws a labelled error on any violation. Returns the resolved absolute
 * path on success.
 */
export function assertSafeWorkspaceOutputDir(workspaceDir: string, outputDir: string, label: string): string {
  if (!outputDir || !outputDir.trim()) {
    throw new Error(`${label} must not be empty.`);
  }
  if (isNetworkPath(workspaceDir) || isNetworkPath(outputDir)) {
    throw new Error(
      `Refusing to use ${label} at '${outputDir}': workspace or output path is a UNC / network path. ` +
        'Use a local drive-letter path.'
    );
  }

  const resolvedOutput = path.isAbsolute(outputDir) ? path.resolve(outputDir) : path.resolve(workspaceDir, outputDir);
  const resolvedWorkspace = path.resolve(workspaceDir).replace(/[\\/]+$/, '');
  const prefix = resolvedWorkspace + path.sep;
  const insideWorkspace =
    resolvedOutput.length > prefix.length && resolvedOutput.toLowerCase().startsWith(prefix.toLowerCase());

  if (!insideWorkspace) {
    throw new Error(
      `${label} ('${outputDir}') resolves to '${resolvedOutput}' which is outside the workspace ` +
        `('${resolvedWorkspace}'). The directory is wiped before each run, so it must be a ` +
        "path strictly inside the workspace. Use a relative path like '.winapp/bindings' or an absolute path " +
        'that descends from the workspace root.'
    );
  }

  if (hasReparsePointOnPath(resolvedOutput, resolvedWorkspace)) {
    throw new Error(
      `${label} ('${outputDir}') resolves through a reparse point under '${resolvedWorkspace}'. ` +
        'Reparse points (symlinks / junctions) are rejected because the wipe could follow them ' +
        'outside the workspace. Move the output to a regular subdirectory.'
    );
  }

  return resolvedOutput;
}
