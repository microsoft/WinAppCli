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
